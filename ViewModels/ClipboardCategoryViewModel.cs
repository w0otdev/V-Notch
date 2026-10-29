using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Controls;
using VNotch.Services;

namespace VNotch.ViewModels;

public sealed partial class ClipboardCategoryViewModel(TrayIconKind iconKind, string? name = null, string? customLabel = null) : ObservableObject
{
    public TrayIconKind IconKind { get; } = iconKind;
    public string Name { get; } = name ?? iconKind.ToString();
    public bool IsPersonal => IconKind == TrayIconKind.Personal;
    public string Label => customLabel ?? Loc.Get("clipboard.category." + Name.ToLowerInvariant());
#pragma warning disable S2325
    public string ReorderHint => Loc.Get("clipboard.reorderLabel");
#pragma warning restore S2325
    public bool IsLightSelection => IsSelected && IconKind is TrayIconKind.All or TrayIconKind.Pin or TrayIconKind.Personal;
    public bool IsAccentSelection => IsSelected && !IsLightSelection;
    public TrayIconKind LockIconKind => IsUnlocked ? TrayIconKind.Unlock : TrayIconKind.Lock;

    public string CountLabel => Count > 99 ? "99+" : Math.Max(0, Count).ToString(Loc.GetCulture());
    [ObservableProperty] private int _count;
    partial void OnCountChanged(int oldValue, int newValue)
    {
        if (Math.Min(Math.Max(0, oldValue), 100) != Math.Min(Math.Max(0, newValue), 100))
            OnPropertyChanged(nameof(CountLabel));
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLightSelection), nameof(IsAccentSelection))]
    private bool _isSelected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockIconKind))]
    private bool _isUnlocked;

    public void ApplyLocalization()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ReorderHint));
        OnPropertyChanged(nameof(CountLabel));
    }
}
