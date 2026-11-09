using System.Windows.Automation;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private IReadOnlyList<BatteryDevice> _batteryDevices = Array.Empty<BatteryDevice>();
    private bool _refreshingBatteryChoices;

    internal void PopulateBatterySources(IReadOnlyList<BatteryDevice>? devices = null, bool preserveSelection = true)
    {
        if (devices != null) _batteryDevices = devices;
        string selectedId = preserveSelection && BatteryDeviceCombo.SelectedValue is string selected
            ? selected : _settings.BatteryDeviceId ?? NotchSettings.SystemBatteryDeviceId;
        bool loading = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            BatteryDeviceLabel.Text = Loc.Get("settings.batteryDevice");
            BatteryDeviceHint.Text = Loc.Get("settings.batteryDevice.hint");
            AutomationProperties.SetName(BatteryDeviceCombo, BatteryDeviceLabel.Text);
            var choices = new List<ComboBoxItem>
            {
                new() { Tag = NotchSettings.SystemBatteryDeviceId, Content = Loc.Get("settings.batteryDevice.system") },
                new() { Tag = NotchSettings.AutoBluetoothBatteryDeviceId, Content = Loc.Get("settings.batteryDevice.auto") }
            };
            foreach (var device in _batteryDevices)
                choices.Add(new ComboBoxItem { Tag = device.Id, Content = device.IsConnected
                    ? $"{device.Name} · {(device.HasReading ? $"{device.Percentage}%" : "—") }"
                    : Loc.Get("settings.batteryDevice.unavailable", device.Name) });
            if (!choices.Any(choice => string.Equals(choice.Tag as string, selectedId, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new ComboBoxItem { Tag = selectedId, Content = Loc.Get("settings.batteryDevice.unavailable", selectedId) });
            BatteryDeviceCombo.ItemsSource = choices;
            BatteryDeviceCombo.SelectedItem = choices.First(choice => string.Equals(choice.Tag as string, selectedId, StringComparison.OrdinalIgnoreCase));
        }
        finally { _isLoadingSettings = loading; }
    }

    private async void BatteryDeviceCombo_DropDownOpened(object sender, EventArgs e)
    {
        if (_refreshingBatteryChoices) return;
        _refreshingBatteryChoices = true;
        try
        {
            var devices = await BluetoothBatteryService.Instance.GetDevicesAsync(forceRefresh: true);
            if (!_isClosing) PopulateBatterySources(devices);
        }
        finally { _refreshingBatteryChoices = false; }
    }

    private void BatteryDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingSettings) PushLivePreview();
    }
}
