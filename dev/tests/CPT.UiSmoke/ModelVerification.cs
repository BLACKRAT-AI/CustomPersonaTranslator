using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using CPT.Core.Models;
using CPT.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CPT.UiSmoke;

internal static class ModelVerification
{
    public static int Run(Application app, string model)
    {
        var result = 1;
        var view = new WebView2();
        var window = new Window { Title = "3D avatar verification", Width = 500, Height = 500, Content = view };
        window.Loaded += async (_, _) => {
            try
            {
                await view.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
                view.CoreWebView2.WebResourceResponseReceived += (_, response) => { if (response.Response.StatusCode >= 400) Console.WriteLine($"HTTP {response.Response.StatusCode}: {response.Request.Uri}"); };
                view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 20, 22, 25);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var rendered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                view.CoreWebView2.WebMessageReceived += (_, e) => {
                    using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var type = doc.RootElement.GetProperty("type").GetString();
                    if (type == "ready") ready.TrySetResult();
                    if (type == "avatar_status")
                    {
                        var status = doc.RootElement.GetProperty("text").GetString()!;
                        if (doc.RootElement.GetProperty("error").GetBoolean()) rendered.TrySetException(new InvalidOperationException(status));
                        else rendered.TrySetResult(status);
                    }
                };
                view.CoreWebView2.SetVirtualHostNameToFolderMapping("avatar-test.cpt", Path.Combine(AppContext.BaseDirectory, "HologramWeb"), CoreWebView2HostResourceAccessKind.DenyCors);
                view.CoreWebView2.Navigate("https://avatar-test.cpt/index.html");
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var message = JsonSerializer.Serialize(AvatarModelBinding.Message(view.CoreWebView2, new VisualConfig { ModelFile = model == "default" ? null : model }));
                view.CoreWebView2.PostWebMessageAsJson(message);
                view.CoreWebView2.PostWebMessageAsJson("{\"type\":\"appear\"}");
                var status = await rendered.Task.WaitAsync(TimeSpan.FromSeconds(45));
                foreach (var style in new[] { "particles", "scanlines", "steam", "lasers" })
                {
                    view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "persona", style, color = "prismatic" }));
                    await Task.Delay(2500);
                    var screenshot = Path.Combine(Path.GetTempPath(), "CPT-hologram-" + style + ".png");
                    using (var output = File.Create(screenshot)) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                    Console.WriteLine("Captured " + style + ": " + screenshot);
                }
                Console.WriteLine("PASS actual WebView head load and all style captures: " + status);
                result = 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); }
            finally { view.Dispose(); window.Close(); app.Shutdown(); }
        };
        window.Show();
        app.Run();
        return result;
    }
}
