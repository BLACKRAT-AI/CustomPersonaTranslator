using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace CPT.Shell;

/// <summary>
/// Creates the one WebView2 environment the whole app shares.
///
/// It has to be shared: every environment on a given user-data folder must be
/// created with identical options, so two windows each building their own would
/// eventually fail as soon as their options diverged.
///
/// The autoplay switch is the reason there are options at all. WebView2 will not
/// let a page start video without a click inside the page, and the clip picker's
/// Play button is outside it -- so without this the video simply never starts.
/// </summary>
internal static class WebViewEnvironment
{
    private static readonly object Gate = new();
    private static Task<CoreWebView2Environment>? _shared;

    /// <summary>The shared environment, created on first use.</summary>
    public static Task<CoreWebView2Environment> GetAsync()
    {
        lock (Gate)
        {
            return _shared ??= CreateAsync();
        }
    }

    private static Task<CoreWebView2Environment> CreateAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomPersonaTranslator", "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
        };

        return CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    }
}
