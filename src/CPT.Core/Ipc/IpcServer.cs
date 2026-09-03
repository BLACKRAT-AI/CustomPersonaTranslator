using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Models;

namespace CPT.Core.Ipc;

// Tiny WebSocket server over HttpListener — no ASP.NET Core dependency.
// Bound to 127.0.0.1 only. Connections tagged by ?adapter=<id>.
public sealed class IpcServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private int _port;

    public event Action<string /*adapter*/, InboundMessage>? MessageReceived;
    public event Action<string /*adapter*/>? ClientConnected;
    public event Action<string /*adapter*/>? ClientDisconnected;

    public int Port => _port;

    public IpcServer(int port = 17872)
    {
        _port = port;
    }

    public void Start()
    {
        // Walk ports until we find one that isn't already held by a previous
        // CPT.Shell instance (or anything else). HttpListenerException 183 is
        // "registration conflict" — meaning HTTP.SYS already has this prefix
        // bound, which happens commonly when an earlier crash didn't clean up.
        const int maxAttempts = 8;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            try { _listener.Start(); break; }
            catch (HttpListenerException ex) when (ex.ErrorCode == 183 || ex.ErrorCode == 32)
            {
                if (attempt == maxAttempts - 1) throw;
                _port++;
            }
        }
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 426;
                ctx.Response.Close();
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(ctx));
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx)
    {
        var adapter = ctx.Request.QueryString["adapter"] ?? Guid.NewGuid().ToString("N")[..8];
        WebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null); }
        catch { ctx.Response.Close(); return; }
        var ws = wsCtx.WebSocket;
        _clients[adapter] = ws;
        ClientConnected?.Invoke(adapter);

        var buffer = new byte[16 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", _cts.Token);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.ToArray());
                InboundMessage? msg = null;
                try { msg = JsonSerializer.Deserialize<InboundMessage>(text); }
                catch { /* ignore malformed */ }
                if (msg is not null) MessageReceived?.Invoke(adapter, msg);
            }
        }
        catch { /* connection closed */ }
        finally
        {
            _clients.TryRemove(adapter, out _);
            ClientDisconnected?.Invoke(adapter);
            try { ws.Dispose(); } catch { }
        }
    }

    public async Task SendAsync(string adapter, object payload)
    {
        if (!_clients.TryGetValue(adapter, out var ws) || ws.State != WebSocketState.Open) return;
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        await ws.SendAsync(json, WebSocketMessageType.Text, true, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        foreach (var c in _clients.Values) try { c.Dispose(); } catch { }
    }
}
