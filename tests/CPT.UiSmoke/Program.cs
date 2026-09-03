using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using CPT.Shell;
using CPT.Shell.Views;

namespace CPT.UiSmoke;

/// <summary>
/// Builds every window and pane in the app and reports which ones throw.
///
/// This exists because the failure it catches is invisible to the compiler and
/// to the unit tests: a XAML resource that no longer resolves, or a constructor
/// that throws on a machine where some tool is missing, shows up only as a
/// window that silently fails to appear. Nothing here is ever shown -- the
/// windows are laid out off-screen and dropped -- so it is safe to run at any
/// time, including on a machine somebody is using.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var application0 = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Length >= 2 && args[0] == "--ring")
        {
            var code = RenderRing(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1.0);
            application0.Shutdown();
            return code;
        }

        var application = application0;
        application.Resources = new ResourceDictionary
        {
            MergedDictionaries =
            {
                Load("Icons.xaml"),
                Load("Theme.xaml"),
            },
        };

        var failures = 0;
        AppServices? services = null;

        failures += Check("AppServices", () => services = new AppServices());
        if (services is null)
        {
            Console.WriteLine("FAIL  services could not start; nothing else can be checked.");
            return 1;
        }

        try
        {
            failures += Check("SettingsWindow", () => Realize(new SettingsWindow(services)));
            failures += Check("PersonaWindow", () => Realize(new PersonaWindow(services)));

            foreach (var (name, make) in Pages(services))
                failures += Check(name, () => Realize(make()));
        }
        finally
        {
            // Disposing kills the llama-server this started. Skipping it leaks a
            // multi-gigabyte process per run, which is how 36 of them ended up
            // orphaned on this machine.
            services.Dispose();
        }

        Console.WriteLine(failures == 0 ? "OK    every window built." : $"FAIL  {failures} did not build.");
        application.Shutdown();
        return failures == 0 ? 0 : 1;
    }


    /// <summary>
    /// Renders the prismatic ring to a PNG without showing anything.
    ///
    /// Laid out exactly as the app lays it out -- a bar-shaped ring with the
    /// window's margin around it -- because the two things worth checking are
    /// whether the corners read as arcs and whether the bloom fades out or gets
    /// cut off square by the edge it is given.
    /// </summary>
    private static int RenderRing(string path, double energy)
    {
        const int width = 460, height = 150;
        const int margin = 26;

        var ring = new PrismaticBorder { CornerRadius = 14 };
        ring.SetPalette("prismatic");
        ring.SetLit(energy > 0);
        ring.AdvanceForTest(energy, phase: 0.0);

        var host = new Grid
        {
            Width = width, Height = height,
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x36)),
        };
        ring.Margin = new Thickness(margin);
        host.Children.Add(ring);

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);

        Console.WriteLine("ok    ring rendered to " + path);
        return 0;
    }

    private static IEnumerable<(string Name, Func<FrameworkElement> Make)> Pages(AppServices services) =>
    [
        ("CliSetupView", () => new CliSetupView(services)),
        ("CloningSetupView", () => new CloningSetupView()),
        ("TestTranslateView", () => new TestTranslateView(services)),
        ("PersonaEditorView", () => new PersonaEditorView(services)),
        ("VoiceClipperView", () => new VoiceClipperView(services)),
    ];

    /// <summary>
    /// Forces the full build: measure and arrange run the templates, the
    /// bindings and the styles, which is where a broken resource surfaces.
    /// </summary>
    private static void Realize(FrameworkElement element)
    {
        if (element is Window window)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;                     // never on a real monitor
            window.Top = -30000;
        }
        element.Measure(new Size(1200, 900));
        element.Arrange(new Rect(0, 0, 1200, 900));
        element.UpdateLayout();
        if (element is Window w) w.Close();
    }

    private static int Check(string name, Action build)
    {
        try
        {
            build();
            Console.WriteLine("ok    " + name);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            if (ex.InnerException is { } inner)
                Console.WriteLine("      inner: " + inner.GetType().Name + ": " + inner.Message);
            Console.WriteLine("      " + (ex.StackTrace ?? "").Split('\n')[0].Trim());
            return 1;
        }
    }

    private static ResourceDictionary Load(string file) =>
        new() { Source = new Uri("pack://application:,,,/CPT.Shell;component/" + file, UriKind.Absolute) };
}
