using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Models;

namespace CPT.Core.Llm;

// Streaming client for llama.cpp's OpenAI-compatible /v1/chat/completions
// endpoint. Drop-in replacement for OllamaClient — same persona prompt
// construction and async-enumerable token stream.
public sealed class LlamaCppClient : IDisposable, IPersonaRewriter
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public LlamaCppClient(string baseUrl = "http://127.0.0.1:18080")
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async IAsyncEnumerable<string> StreamRewriteAsync(
        Persona persona, string text, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            messages = BuildMessages(persona, text),
            stream = true,
            temperature = 0.7,
            max_tokens = 600,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // SSE: each event is `data: {json}\n\n`, terminated by `data: [DONE]`.
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload == "[DONE]") yield break;
            string? chunk = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) continue;
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var c)) chunk = c.GetString();
            }
            catch (JsonException) { continue; }
            if (!string.IsNullOrEmpty(chunk)) yield return chunk!;
        }
    }

    /// <summary>
    /// The rewrite task, stated after the persona's own prompt.
    ///
    /// Order matters with a small local model: a generated persona prompt is
    /// usually written in the second person ("you are the ship's computer"),
    /// which invites the model to ANSWER the text rather than restate it. The
    /// last instruction it reads has to be the job, not the character.
    /// </summary>
    private const string TaskRule =
        "TASK. The text below is an answer that has ALREADY been produced by a coding agent. " +
        "Your only job is to say that same answer again in the persona's voice. " +
        "Do not answer it, do not reply to it, do not ask anything back, and do not add or " +
        "invent information. Keep every fact, number, name, file path and command exactly as " +
        "given. If the text is a question, restate the question — do not answer it. " +
        "Output only the restated text, with no preamble.";

    private static object[] BuildMessages(Persona persona, string text)
    {
        var list = new List<object>();

        if (!string.IsNullOrWhiteSpace(persona.SystemPrompt))
            list.Add(new { role = "system", content = persona.SystemPrompt });

        if (persona.FewShotQuotes.Count > 0)
        {
            var sb = new StringBuilder("Reference quotes from this persona (style only, do not copy):\n");
            int take = Math.Min(5, persona.FewShotQuotes.Count);
            for (int i = 0; i < take; i++) sb.Append("- ").AppendLine(persona.FewShotQuotes[i]);
            list.Add(new { role = "system", content = sb.ToString() });
        }

        list.Add(new { role = "system", content = TaskRule });

        // Fenced, so the model cannot read the agent's answer as something
        // addressed to it.
        list.Add(new
        {
            role = "user",
            content = "<<<ANSWER\n" + text + "\nANSWER>>>\n\nSay that in the persona's voice.",
        });
        return list.ToArray();
    }


    /// <summary>Releases the HTTP client this instance owns.</summary>
    public void Dispose() => _http.Dispose();
}
