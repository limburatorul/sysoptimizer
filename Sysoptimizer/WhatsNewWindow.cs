using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Sysoptimizer.Services;

namespace Sysoptimizer;

/// <summary>
/// What's new, as the studio's other apps show it: every release in a list on the left, under the month it came out,
/// and the chosen one on the right under its 960 x 170 banner. The notes are whatsnew.txt, built into the exe, so it
/// works offline and shows every release. A release with a picture of its own (WhatsNew\x.y.jpg, made at 1920 x 340
/// from the studio's banner template, the month it came out) shows it; older ones get the app's colours and version.
/// </summary>
public sealed class WhatsNewWindow : Window
{
    private sealed record Release(string Version, DateTime Date, string Title, List<string> Lines);

    private static WhatsNewWindow? _open;
    private readonly List<Release> _releases = Load();
    private readonly StackPanel _nav = new();
    private readonly Border _banner = new() { CornerRadius = new CornerRadius(10), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _notes = new();
    private readonly ScrollViewer _notesScroll;
    private int _shown = -1;

    /// <summary>Brings the window up, or to the front when it is already open.</summary>
    public static void Open()
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new WhatsNewWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    /// <summary>
    /// Once after an update. A first install has nothing new to tell; a PC that recorded before today, which never
    /// wrote down a version (everything before 1.19.3), is an update.
    /// </summary>
    public static void OpenOnceAfterUpdate()
    {
        string current = Updater.Current.ToString(3);
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Sysoptimizer");
        string? seen = key.GetValue("LastSeenVersion") as string;
        if (seen == current) return;
        key.SetValue("LastSeenVersion", current);
        if (seen != null || HistoryStore.OldestDay() < DateTime.Today) Open();
    }

    private static List<Release> Load()
    {
        using var stream = typeof(WhatsNewWindow).Assembly.GetManifestResourceStream("Sysoptimizer.whatsnew.txt");
        using var reader = new StreamReader(stream!);
        var list = new List<Release>();
        foreach (var line in reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("## ") && line[3..].Split(" | ", 3) is [var v, var d, var t])
                list.Add(new Release(v.Trim(), DateTime.ParseExact(d.Trim(), "yyyy-MM-dd", null), t.Trim(), new()));
            else if (line.StartsWith("# ") || line.Trim() == "" || list.Count == 0) continue;     // comments, blank lines
            else list[^1].Lines.Add(line);
        }
        return list;
    }

    private WhatsNewWindow()
    {
        Title = "What's new — Sysoptimizer";
        Icon = new BitmapImage(new Uri("pack://application:,,,/app.ico"));
        Width = 1240; Height = 720; MinWidth = 700; MinHeight = 420;     // room for the list and a 960 x 170 banner
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        FontSize = 13;
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        SourceInitialized += (_, _) => WindowGlass.EnableAcrylic(this);
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.Down or Key.Up) { Show(Math.Clamp(_shown + (e.Key == Key.Down ? 1 : -1), 0, _releases.Count - 1)); e.Handled = true; }
        };

        var close = new Button { Content = "Close", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => Close();

        var navScroll = new ScrollViewer { Content = _nav, Style = (Style)FindResource("GlassScrollViewer"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 200, Margin = new Thickness(0, 0, 16, 0) };
        _notesScroll = new ScrollViewer { Content = _notes, Style = (Style)FindResource("GlassScrollViewer"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        var right = new DockPanel();
        DockPanel.SetDock(_banner, Dock.Top);
        right.Children.Add(_banner);
        right.Children.Add(_notesScroll);

        var body = new DockPanel { Margin = new Thickness(18, 16, 18, 14) };
        DockPanel.SetDock(close, Dock.Bottom);
        DockPanel.SetDock(navScroll, Dock.Left);
        body.Children.Add(close);
        body.Children.Add(navScroll);
        body.Children.Add(right);
        var ground = new Border { Child = body };
        ground.SetResourceReference(Border.BackgroundProperty, "Surface");
        Content = ground;

        BuildNav();
        // the version running, which is the newest unless this build is older than its own notes
        Show(Math.Max(0, _releases.FindIndex(r => r.Version == Updater.Current.ToString(2))));
    }

    private void OnThemeChanged() => WindowGlass.ApplyChrome(this);

    private void BuildNav()
    {
        string? month = null;
        for (int i = 0; i < _releases.Count; i++)
        {
            var r = _releases[i];
            var m = r.Date.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
            if (m != month)
            {
                month = m;
                _nav.Children.Add(Dim(new TextBlock { Text = m, FontSize = 11, Margin = new Thickness(8, _nav.Children.Count == 0 ? 0 : 12, 0, 4) }));
            }
            int index = i;
            var row = new DockPanel { Width = 166 };      // the button's template centres its content: the row spans it
            var when = Dim(new TextBlock { Text = i == 0 ? "Latest" : r.Date.ToString("d MMM", CultureInfo.InvariantCulture), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);
            row.Children.Add(new TextBlock { Text = r.Version, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = row, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), Padding = new Thickness(0, 7, 0, 7), Margin = new Thickness(0, 1, 0, 1), ToolTip = r.Title };
            button.SetResourceReference(ForegroundProperty, "TextPrimary");
            System.Windows.Automation.AutomationProperties.SetName(button, $"{r.Version}, {r.Title}");
            button.Click += (_, _) => Show(index);
            _nav.Children.Add(button);
        }
    }

    private void Show(int i)
    {
        if (i == _shown || i < 0 || i >= _releases.Count) return;
        _shown = i;
        var r = _releases[i];
        var buttons = _nav.Children.OfType<Button>().ToList();
        for (int b = 0; b < buttons.Count; b++)
            if (b == i) buttons[b].SetResourceReference(BackgroundProperty, "AccentDim");
            else buttons[b].Background = Brushes.Transparent;
        buttons[i].BringIntoView();

        _banner.Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = Banner(r) };
        _notes.Children.Clear();
        _notes.Children.Add(Dim(new TextBlock { Text = r.Date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 0, 8) }));
        foreach (var line in r.Lines)
        {
            var text = line.Trim();
            if (text.StartsWith("- "))
            {
                var point = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var dot = new TextBlock { Text = "•", Margin = new Thickness(2, 0, 10, 0) };
                dot.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                DockPanel.SetDock(dot, Dock.Left);
                point.Children.Add(dot);
                point.Children.Add(Rich(text[2..]));
                _notes.Children.Add(point);
            }
            else if (text.StartsWith("**") && text.EndsWith("**") && text.Length > 4 && !text[2..^2].Contains("**"))
                _notes.Children.Add(new TextBlock { Text = text[2..^2], FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 4) });
            else
            {
                var p = Rich(text);
                p.Margin = new Thickness(0, 4, 0, 6);
                _notes.Children.Add(p);
            }
        }
        _notesScroll.ScrollToTop();
    }

    private static TextBlock Dim(TextBlock t)
    {
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return t;
    }

    /// <summary>A line of the notes with its **bold** parts in bold.</summary>
    private static TextBlock Rich(string text)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
            if (parts[i] != "") t.Inlines.Add(new Run(parts[i]) { FontWeight = i % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal });
        return t;
    }

    /// <summary>The release's picture (x.y.jpg, built in), or the app's colours under its version and title.</summary>
    private static UIElement Banner(Release r)
    {
        using var stream = typeof(WhatsNewWindow).Assembly.GetManifestResourceStream($"Sysoptimizer.WhatsNew.{r.Version}.jpg");
        if (stream is not null)
        {
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.StreamSource = stream;
            picture.EndInit();
            picture.Freeze();
            return new Image { Source = picture, Width = 960, Height = 170, Stretch = Stretch.UniformToFill };
        }

        // the banner template's Sysoptimizer colours: teal and sky over a dark teal ground, a graph running through it
        var ground = new LinearGradientBrush(Color.FromRgb(0x09, 0x18, 0x17), Color.FromRgb(0x0C, 0x24, 0x24), 20);
        var glow = new System.Windows.Shapes.Ellipse
        {
            Width = 560, Height = 380,
            Fill = new RadialGradientBrush(Color.FromArgb(0x55, 0x2D, 0xD4, 0xBF), Color.FromArgb(0, 0x2D, 0xD4, 0xBF)),
        };
        var art = new Canvas { Width = 960, Height = 170, ClipToBounds = true };
        Canvas.SetLeft(glow, 520); Canvas.SetTop(glow, -150);
        art.Children.Add(glow);
        var v = r.Version.Split('.').Select(int.Parse).ToArray();
        var rnd = new Random(v[0] * 100 + v[1]);     // seeded by the version: a release looks the same every time
        var line = new PointCollection();
        double y = 110;
        for (int x = 360; x <= 960; x += 20) { y = Math.Clamp(y + (rnd.NextDouble() - 0.5) * 40, 60, 150); line.Add(new Point(x, y)); }
        art.Children.Add(new System.Windows.Shapes.Polyline { Points = line, Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0x2D, 0xD4, 0xBF)), StrokeThickness = 2 });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0, 0, 0), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        words.Children.Add(new TextBlock { Text = "WHAT’S NEW IN", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)) });
        words.Children.Add(new TextBlock { Text = r.Version, FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        words.Children.Add(new TextBlock { Text = r.Title, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)) });
        return new Grid { Width = 960, Height = 170, Background = ground, Children = { art, words } };
    }
}
