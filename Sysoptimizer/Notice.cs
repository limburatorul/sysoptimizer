using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Sysoptimizer;

/// <summary>
/// Sysoptimizer's own notifications, drawn in the chosen theme instead of Windows' balloons (the user's choice, 07.11,
/// like Launchpad's): a card in the corner above the taskbar with the icon, the text and a button. It never takes the
/// focus, waits while a full-screen game or a presentation runs (or Focus assist says not now), stays while the pointer
/// is over it and leaves on its own after a while. Clicking the card does what its button does.
/// </summary>
public sealed class Notice : Window
{
    private static readonly List<Notice> Open = new();
    private static readonly Queue<(string Title, string Body, bool Warning, string Action, Action OnAction)> Waiting = new();
    private static DispatcherTimer? _retry;
    private readonly DispatcherTimer _life;
    private readonly string _key;
    private bool _leaving;

    /// <summary>Shows the notice now, or as soon as the user can be interrupted; the same text twice shows once.</summary>
    public static void Post(string title, string body, bool warning, string action, Action onAction)
    {
        string key = title + "\n" + body;
        if (Waiting.Any(w => w.Title + "\n" + w.Body == key) || Open.Any(n => n._key == key)) return;
        Waiting.Enqueue((title, body, warning, action, onAction));
        Pump();
    }

    private static void Pump()
    {
        while (Waiting.Count > 0 && CanInterrupt() && Open.Count < 3)
        {
            var w = Waiting.Dequeue();
            new Notice(w.Title, w.Body, w.Warning, w.Action, w.OnAction).Appear();
        }
        if (Waiting.Count > 0 && _retry is null)
        {
            _retry = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _retry.Tick += (_, _) => { _retry!.Stop(); _retry = null; Pump(); };
            _retry.Start();
        }
    }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>QUNS_ACCEPTS_NOTIFICATIONS only: not during a full-screen app, a presentation or quiet hours.</summary>
    private static bool CanInterrupt() => SHQueryUserNotificationState(out int state) != 0 || state == 5;

    private Notice(string title, string body, bool warning, string action, Action onAction)
    {
        _key = title + "\n" + body;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        FontSize = 13;
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(ForegroundProperty, "TextPrimary");

        var card = new Border
        {
            BorderThickness = new Thickness(1), Margin = new Thickness(10), ClipToBounds = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Image
        {
            Source = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico")), Width = 36, Height = 36,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 14, 12, 0),
        };
        grid.Children.Add(icon);

        var text = new StackPanel { Margin = new Thickness(0, 12, 10, 12) };
        Grid.SetColumn(text, 1);
        var top = new DockPanel();
        var close = new Button
        {
            Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, ToolTip = "Close",
            Padding = new Thickness(6), Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Top,
            Template = BareButton(),
        };
        close.SetResourceReference(ForegroundProperty, "TextSecondary");
        System.Windows.Automation.AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => Leave();
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);
        var heading = new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 6, 0) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, warning ? "StatusWarn" : "TextPrimary");
        top.Children.Add(heading);
        text.Children.Add(top);
        var said = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        said.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        text.Children.Add(said);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var primary = new Button { Content = action, Padding = new Thickness(16, 7, 16, 7) };
        primary.SetResourceReference(StyleProperty, "PrimaryButton");
        primary.Click += (_, _) => { onAction(); Leave(); };
        var later = new Button { Content = "Dismiss", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0) };
        later.Click += (_, _) => Leave();
        buttons.Children.Add(primary);
        buttons.Children.Add(later);
        text.Children.Add(buttons);
        grid.Children.Add(text);
        card.Child = grid;
        Content = card;

        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is Button) return;
            onAction();
            Leave();
        };

        _life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(warning ? 30 : 20) };
        _life.Tick += (_, _) => { if (!IsMouseOver) Leave(); };
        MouseLeave += (_, _) => { _life.Stop(); _life.Start(); };
        SourceInitialized += (_, _) =>
        {
            // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: never steals the focus from a game or a text being typed, and stays out of Alt+Tab
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x80);
        };
    }

    /// <summary>Just the content, so the close glyph doesn't get the boxed look of the app's buttons.</summary>
    private static ControlTemplate BareButton()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private void Appear()
    {
        Open.Add(this);
        Left = -10000; Top = -10000;
        Show();
        Place();
        var slide = new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var shift = new TranslateTransform();
        ((FrameworkElement)Content).RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.XProperty, slide);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        _life.Start();
    }

    /// <summary>Bottom right of the screen the taskbar is on, the newest at the bottom, the others above it.</summary>
    private static void Place()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 6;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var n = Open[i];
            n.Left = area.Right - n.ActualWidth - 6;
            n.Top = bottom - n.ActualHeight;
            bottom = n.Top;
        }
    }

    private void Leave()
    {
        if (_leaving) return;
        _leaving = true;
        _life.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { Close(); Open.Remove(this); Place(); Pump(); };
        BeginAnimation(OpacityProperty, fade);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
