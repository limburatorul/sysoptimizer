using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Sysoptimizer;

/// <summary>A card with two stacked graphs (speed + latency) for one disk or network adapter.</summary>
public class MetricCard
{
    public Border Root { get; private init; } = null!;
    public TextBlock SpeedValueText { get; private init; } = null!;
    public TextBlock LatencyValueText { get; private init; } = null!;
    public Grid SpeedGraph { get; private init; } = null!;
    public Grid LatencyGraph { get; private init; } = null!;
    public Path SpeedLine { get; private init; } = null!;
    public Path SpeedFill { get; private init; } = null!;
    public Path LatencyLine { get; private init; } = null!;
    public Path LatencyFill { get; private init; } = null!;
    public Queue<(DateTime Time, double Value)> SpeedHistory { get; } = new();
    public Queue<(DateTime Time, double Value)> LatencyHistory { get; } = new();

    public static MetricCard Build(FrameworkElement resourceSource, string title, string speedLabel, string latencyLabel)
    {
        var accent = (Brush)resourceSource.FindResource("Accent");
        var areaFill = (Brush)resourceSource.FindResource("AreaFill");
        var textSecondary = (Brush)resourceSource.FindResource("TextSecondary");
        var border = (Brush)resourceSource.FindResource("Border");
        var glow = (System.Windows.Media.Effects.Effect)resourceSource.FindResource("LineGlow");

        var (speedGraph, speedLine, speedFill) = BuildGraph(accent, areaFill, glow);
        var (latencyGraph, latencyLine, latencyFill) = BuildGraph(accent, areaFill, glow);

        var speedValueText = new TextBlock { Foreground = textSecondary, FontSize = 11 };
        var latencyValueText = new TextBlock { Foreground = textSecondary, FontSize = 11 };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(BuildSectionHeader(speedLabel, speedValueText, textSecondary));
        stack.Children.Add(WrapGraph(speedGraph, 70));
        stack.Children.Add(BuildSectionHeader(latencyLabel, latencyValueText, textSecondary, topMargin: 10));
        stack.Children.Add(WrapGraph(latencyGraph, 70));

        var root = new Border
        {
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Child = stack,
        };

        return new MetricCard
        {
            Root = root,
            SpeedValueText = speedValueText,
            LatencyValueText = latencyValueText,
            SpeedGraph = speedGraph,
            LatencyGraph = latencyGraph,
            SpeedLine = speedLine,
            SpeedFill = speedFill,
            LatencyLine = latencyLine,
            LatencyFill = latencyFill,
        };
    }

    private static DockPanel BuildSectionHeader(string label, TextBlock valueText, Brush textSecondary, double topMargin = 0)
    {
        var panel = new DockPanel { Margin = new Thickness(0, topMargin, 0, 0) };
        DockPanel.SetDock(valueText, Dock.Right);
        panel.Children.Add(valueText);
        panel.Children.Add(new TextBlock { Text = label, Foreground = textSecondary, FontSize = 11 });
        return panel;
    }

    private static Border WrapGraph(Grid graph, double height) =>
        new() { Child = graph, Height = height, Margin = new Thickness(0, 4, 0, 0), ClipToBounds = true };

    private static (Grid graph, Path line, Path fill) BuildGraph(Brush accent, Brush areaFill, System.Windows.Media.Effects.Effect glow)
    {
        var fill = new Path { Fill = areaFill, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var line = new Path
        {
            Stroke = accent,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Effect = glow,
        };
        var graph = new Grid();
        graph.Children.Add(fill);
        graph.Children.Add(line);
        return (graph, line, fill);
    }
}
