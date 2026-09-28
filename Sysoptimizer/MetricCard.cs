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

    // Every color, radius and weight is a resource reference, so a theme swap repaints existing cards.
    private static T Ref<T>(T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static MetricCard Build(string title, string speedLabel, string latencyLabel)
    {
        var (speedGraph, speedLine, speedFill) = BuildGraph();
        var (latencyGraph, latencyLine, latencyFill) = BuildGraph();

        var speedValueText = Ref(new TextBlock { FontSize = 11 }, TextBlock.ForegroundProperty, "TextSecondary");
        var latencyValueText = Ref(new TextBlock { FontSize = 11 }, TextBlock.ForegroundProperty, "TextSecondary");

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(BuildSectionHeader(speedLabel, speedValueText));
        stack.Children.Add(WrapGraph(speedGraph, 70));
        stack.Children.Add(BuildSectionHeader(latencyLabel, latencyValueText, topMargin: 10));
        stack.Children.Add(WrapGraph(latencyGraph, 70));

        var root = new Border
        {
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        root.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        root.SetResourceReference(Border.BorderBrushProperty, "Border");
        root.SetResourceReference(Border.BackgroundProperty, "CardBackground");

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

    private static DockPanel BuildSectionHeader(string label, TextBlock valueText, double topMargin = 0)
    {
        var panel = new DockPanel { Margin = new Thickness(0, topMargin, 0, 0) };
        DockPanel.SetDock(valueText, Dock.Right);
        panel.Children.Add(valueText);
        panel.Children.Add(Ref(new TextBlock { Text = label, FontSize = 11 }, TextBlock.ForegroundProperty, "TextSecondary"));
        return panel;
    }

    private static Border WrapGraph(Grid graph, double height) =>
        new() { Child = graph, Height = height, Margin = new Thickness(0, 4, 0, 0), ClipToBounds = true };

    private static (Grid graph, Path line, Path fill) BuildGraph()
    {
        var fill = new Path { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        fill.SetResourceReference(Shape.FillProperty, "AreaFill");
        var line = new Path
        {
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        line.SetResourceReference(Shape.StrokeProperty, "Accent");
        line.SetResourceReference(Shape.StrokeThicknessProperty, "GraphStroke");
        line.SetResourceReference(UIElement.EffectProperty, "LineGlow");
        var graph = new Grid();
        graph.Children.Add(fill);
        graph.Children.Add(line);
        return (graph, line, fill);
    }
}
