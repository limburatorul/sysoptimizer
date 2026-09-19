namespace Sysoptimizer.Models;

public class OptionItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsChecked { get; set; }
    public string Note { get; set; } = "";
    public object? Tag { get; set; }
}
