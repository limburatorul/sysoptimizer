using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sysoptimizer.Services;

namespace Sysoptimizer.Models;

/// <summary>
/// One app in the Processes tab. Updated in place every refresh (not rebuilt), so a ticked checkbox and
/// the scroll position survive the list re-sorting under the cursor.
/// </summary>
public sealed class ProcessRow : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public bool IsChecked { get; set; }
    public ProcessUsage Usage { get; private set; } = null!;

    public string Title => Usage.Instances > 1 ? $"{Name} ({Usage.Instances})" : Name;
    public string Description => Usage.Description is { } d
        ? (Usage.Company is { } c ? $"{d} · {c}" : d)
        : Usage.Path ?? "";
    public string CpuText => $"{Usage.Cpu:0.0}%";
    public string RamText => Usage.RamMB >= 1024 ? $"{Usage.RamMB / 1024:0.0} GB" : $"{Usage.RamMB:0} MB";
    public string GpuText => Usage.Gpu >= 0.05 ? $"{Usage.Gpu:0.0}%" : "—";

    // The exe's icon and signature, filled in when AppIdentity has worked them out (null until then).
    private AppIdentity.Info? _identity;
    public System.Windows.Media.ImageSource? Icon => _identity?.Icon;
    public string? Badge => _identity?.Signature switch
    {
        SignatureState.Unsigned => "Unsigned", SignatureState.Untrusted => "Bad signature", _ => null,
    };
    public System.Windows.Visibility BadgeVisibility => Badge == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    private void LookUpIdentity()
    {
        _identity = Usage.Path is { } path
            ? AppIdentity.Get(path, ready => { if (Usage.Path == ready) LookUpIdentity(); })
            : null;
        Raise(nameof(Icon));
        Raise(nameof(Badge));
        Raise(nameof(BadgeVisibility));
    }

    public void Update(ProcessUsage usage)
    {
        bool infoChanged = Usage == null || usage.Path != Usage.Path || usage.Description != Usage.Description;
        Usage = usage;
        Raise(nameof(Title));
        Raise(nameof(CpuText));
        Raise(nameof(RamText));
        Raise(nameof(GpuText));
        if (infoChanged)
        {
            Raise(nameof(Description));
            LookUpIdentity();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
