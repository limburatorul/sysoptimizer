namespace Sysoptimizer.Models;

public class Tweak
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Func<bool> IsApplied { get; set; } = () => false;
    public Action Apply { get; set; } = () => { };
    public Action Revert { get; set; } = () => { };
}
