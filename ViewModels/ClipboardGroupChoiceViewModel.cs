using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Services;

namespace VNotch.ViewModels;

public sealed partial class ClipboardGroupChoiceViewModel : ObservableObject
{
    private Action<ClipboardGroupChoiceViewModel>? _selectionChanged;
    public string Name { get; }
    public string Label => Loc.Get("clipboard.category." + Name.ToLowerInvariant());
    public string? ActiveName => _selectionChanged == null ? null : Name;
    [ObservableProperty] private bool _isSelected;

    public ClipboardGroupChoiceViewModel(string name, bool selected, Action<ClipboardGroupChoiceViewModel> selectionChanged)
    {
        Name = name;
        _isSelected = selected;
        _selectionChanged = selectionChanged;
    }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke(this);

    public void Deactivate()
    {
        _selectionChanged = null;
        OnPropertyChanged(nameof(ActiveName));
    }

    public void ApplyLocalization() => OnPropertyChanged(nameof(Label));
}
