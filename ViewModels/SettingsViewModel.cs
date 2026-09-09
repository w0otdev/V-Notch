using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _service;
    [ObservableProperty]
    private NotchSettings _value;

    [ObservableProperty]
    private double _collapsedWidth;

    [ObservableProperty]
    private double _collapsedHeight;

    [ObservableProperty]
    private double _cornerRadiusCollapsed;

    public event EventHandler<NotchSettings>? Applied;

    public SettingsViewModel(ISettingsService service)
    {
        _service = service;
        _value = service.Load();
        UpdateDerived(_value);
    }

    public async Task ApplyAsync(NotchSettings settings)
    {
        await _service.SaveAsync(settings);
        Value = settings;
        UpdateDerived(settings);
        Applied?.Invoke(this, settings);
    }

    public void Apply(NotchSettings settings)
    {
        _service.Save(settings);
        Value = settings;
        UpdateDerived(settings);
        Applied?.Invoke(this, settings);
    }

    public NotchSettings Load() => _service.Load();
    public void Save(NotchSettings settings) => _service.Save(settings);
    public Task SaveAsync(NotchSettings settings) => _service.SaveAsync(settings);

    private void UpdateDerived(NotchSettings settings)
    {
        CollapsedWidth = settings.EnableDynamicIslandMode ? settings.DynamicIslandWidth : settings.Width;
        CollapsedHeight = settings.Height;
        CornerRadiusCollapsed = settings.CornerRadius;
    }
}
