using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CPT.Core.Models;
using Microsoft.Web.WebView2.Core;

namespace CPT.Shell;

internal static class AvatarModelBinding
{
    private static readonly ConditionalWeakTable<CoreWebView2, Dictionary<string, string>> Files = new();

    public static object Message(CoreWebView2 web, VisualConfig visual)
    {
        string? url = null;
        if (!string.IsNullOrWhiteSpace(visual.ModelFile))
        {
            var path = Path.GetFullPath(visual.ModelFile);
            if (!File.Exists(path)) throw new FileNotFoundException("3D model not found. Choose it again in persona settings.", path);
            var files = Files.GetValue(web, view => {
                var selected = new Dictionary<string, string>(StringComparer.Ordinal);
                view.AddWebResourceRequestedFilter("https://persona-model.invalid/*", CoreWebView2WebResourceContext.All);
                view.WebResourceRequested += (_, args) => {
                    if (!selected.TryGetValue(args.Request.Uri, out var source))
                    { args.Response = view.Environment.CreateWebResourceResponse(null, 404, "Model not found", "Access-Control-Allow-Origin: *"); return; }
                    try { args.Response = view.Environment.CreateWebResourceResponse(File.OpenRead(source), 200, "OK", "Content-Type: model/gltf-binary\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-store"); }
                    catch (IOException) { args.Response = view.Environment.CreateWebResourceResponse(null, 404, "Model not found", ""); }
                };
                return selected;
            });
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + File.GetLastWriteTimeUtc(path).Ticks)));
            url = "https://persona-model.invalid/" + key + ".glb";
            files[url] = path;
        }
        return new { type = "persona", model = url, color = visual.HologramColor, style = visual.HologramStyle,
            framing = new { mode = visual.ModelFraming, fraction = visual.ModelHeadFraction, rotation = visual.ModelRotation, zoom = visual.ModelZoom } };
    }
}
