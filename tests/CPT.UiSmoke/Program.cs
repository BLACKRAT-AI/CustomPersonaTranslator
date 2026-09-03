using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;
using CPT.Core.Agents;
using CPT.Core.Cli;
using CPT.Core.Models;
using System.Windows.Media.Imaging;
using System.IO;
using CPT.Shell;
using CPT.Shell.Controls;
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

        failures += CheckColorWheel();
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

            failures += CheckAgentPersonaPicker(services);
            failures += CheckRewriteOptions(services);
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


    /// <summary>
    /// Round-trips the colour wheel's conversions.
    ///
    /// Checked here rather than in the unit tests because the maths lives in a
    /// WPF control: a hue that does not survive a round trip makes the marker
    /// land somewhere the user did not click.
    /// </summary>
    private static int CheckColorWheel()
    {
        var failures = 0;
        foreach (var hex in new[] { "#6FC2D6", "#E0A03C", "#63D68A", "#B388FF", "#FFFFFF", "#101010" })
        {
            var original = (Color)ColorConverter.ConvertFromString(hex);
            var (h, s, l) = ColorWheel.ToHsl(original);
            var round = ColorWheel.FromHsl(h, s, l);

            var drift = Math.Max(Math.Abs(original.R - round.R),
                        Math.Max(Math.Abs(original.G - round.G), Math.Abs(original.B - round.B)));

            if (drift <= 1) continue;
            Console.WriteLine($"FAIL  colour {hex} round-tripped to #{round.R:X2}{round.G:X2}{round.B:X2}");
            failures++;
        }

        Console.WriteLine(failures == 0 ? "ok    ColorWheel round-trips" : "FAIL  ColorWheel");
        return failures;
    }


    /// <summary>
    /// Drives the agent row's persona picker the way a user does.
    ///
    /// This exists because "the picker does not stick" was diagnosed twice from
    /// reading the XAML and fixed twice without being reproduced. Building the
    /// window and setting the selection is the only way to know.
    /// </summary>
    private static int CheckAgentPersonaPicker(AppServices services)
    {
        var personas = services.Personas.LoadAll().ToList();
        if (personas.Count == 0)
        {
            Console.WriteLine("skip  agent persona picker: no personas on this machine");
            return 0;
        }

        // A throwaway agent, removed again below so the user's list is untouched.
        var agent = new AgentProfile { Name = "smoke", PersonaId = "", ProviderId = "claude-code" };
        services.Settings.Agents.Agents.Add(agent);

        try
        {
            var window = new SettingsWindow(services);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;
            window.Top = -30000;
            window.Measure(new Size(1200, 900));
            window.Arrange(new Rect(0, 0, 1200, 900));
            window.UpdateLayout();

            var list = FindByName<ListBox>(window, "AgentList");
            if (list is null) { Console.WriteLine("FAIL  agent picker: AgentList not found"); return 1; }

            // Offscreen there is no render pass, so a virtualizing panel generates
            // no containers at all. Turning virtualization off for the check makes
            // the rows real without changing what the app does.
            VirtualizingPanel.SetIsVirtualizing(list, false);
            list.UpdateLayout();
            list.Measure(new Size(900, 700));
            list.Arrange(new Rect(0, 0, 900, 700));
            list.UpdateLayout();

            var container = list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) as ListBoxItem;
            container?.ApplyTemplate();
            container?.UpdateLayout();
            if (container is null)
            {
                Console.WriteLine("FAIL  agent picker: no row container"
                    + " (items=" + list.Items.Count
                    + ", status=" + list.ItemContainerGenerator.Status
                    + ", hasSource=" + (list.ItemsSource is not null)
                    + ", actualHeight=" + list.ActualHeight + ")");
                return 1;
            }

            var combos = Descendants<ComboBox>(container).ToList();
            // Identified by what it offers, not by how it is styled: the styling
            // is exactly what keeps changing.
            var picker = combos.FirstOrDefault(c => c.Items.OfType<Persona>().Any());
            if (picker is null)
            {
                Console.WriteLine($"FAIL  agent picker: not found among {combos.Count} combo boxes");
                return 1;
            }

            if (picker.Items.Count == 0)
            {
                Console.WriteLine("FAIL  agent picker: no personas offered");
                return 1;
            }

            // Pick one, exactly as the user does, and see whether it stuck.
            var wanted = personas[0];
            picker.SelectedValue = wanted.Id;
            window.UpdateLayout();

            var failures = 0;
            if (!ReferenceEquals(picker.SelectedItem, picker.Items.OfType<Persona>().FirstOrDefault(p => p.Id == wanted.Id)))
            {
                Console.WriteLine("FAIL  agent picker: the control did not hold the selection");
                failures++;
            }

            // What the CLOSED box actually renders. Checking SelectedItem is not
            // enough: with the retemplated ComboBox, DisplayMemberPath does not
            // reach the selection box, so it silently fell back to ToString()
            // and displayed "CPT.Core.Models.Persona" over a correct selection.
            var shown = SelectionBoxText(picker);
            if (shown != wanted.Name)
            {
                Console.WriteLine($"FAIL  agent picker: box shows '{shown}', expected '{wanted.Name}'");
                failures++;
            }

            if (agent.PersonaId != wanted.Id)
            {
                Console.WriteLine($"FAIL  agent picker: profile still says '{agent.PersonaId}'");
                failures++;
            }

            window.Close();
            Console.WriteLine(failures == 0
                ? $"ok    agent persona picker ({picker.Items.Count} personas, selection stuck)"
                : "FAIL  agent persona picker");
            return failures;
        }
        finally
        {
            // Closing the window persists the agent list, so removing the throwaway
            // from memory is not enough -- it has to be written back out, or a
            // smoke run leaves "smoke" agents in the user's real settings.
            services.Settings.Agents.Agents.Remove(agent);
            services.Settings.Save();
        }
    }


    /// <summary>
    /// Checks the persona voice's own option list is actually populated.
    ///
    /// Same failure as the persona picker and found the same way: the tab was
    /// never loaded, so the UI said the CLI exposed no options when it exposes
    /// several.
    /// </summary>
    private static int CheckRewriteOptions(AppServices services)
    {
        var window = new SettingsWindow(services)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
        };
        window.Measure(new Size(1200, 900));
        window.Arrange(new Rect(0, 0, 1200, 900));
        window.UpdateLayout();

        var provider = FindByName<ComboBox>(window, "RewriteProvider");
        var options = FindByName<ItemsControl>(window, "RewriteOptions");
        var expected = CliOrchestrator.AvailableProviders
            .FirstOrDefault(p => p.Id == (string?)provider?.SelectedValue)?.Options.Count ?? 0;

        var actual = options?.Items.Count ?? -1;
        window.Close();

        if (provider?.SelectedValue is null || actual != expected)
        {
            Console.WriteLine($"FAIL  persona voice options: {actual} shown, {expected} expected");
            return 1;
        }

        Console.WriteLine($"ok    persona voice options ({actual} for {provider.SelectedValue})");
        return 0;
    }

    /// <summary>
    /// The text a closed ComboBox is showing, read out of its visual tree.
    ///
    /// The only way to catch a selection box that is rendering ToString(): the
    /// selection is correct, the binding is correct, and the user still sees a
    /// type name.
    /// </summary>
    private static string SelectionBoxText(ComboBox combo)
    {
        combo.ApplyTemplate();
        combo.UpdateLayout();

        foreach (var presenter in Descendants<ContentPresenter>(combo))
        {
            if (presenter.Content is null) continue;
            presenter.ApplyTemplate();
            var text = Descendants<TextBlock>(presenter).FirstOrDefault();
            if (text is not null) return text.Text;
        }

        return combo.SelectionBoxItem?.ToString() ?? "";
    }
    private static T? FindByName<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? Descendants<T>(root).FirstOrDefault(e => e.Name == name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
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
