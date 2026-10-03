using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    #region Visualizer Audio Device

    private void SetVisualizerAudioDevicePlaceholder()
    {
        VisualizerAudioCombo.ItemsSource = new[]
        {
            new AudioDeviceItem { Id = _settings.VisualizerAudioDeviceId, Name = Loc.Get("settings.visualizerAudio.default") }
        };
        VisualizerAudioCombo.DisplayMemberPath = "Name";
        VisualizerAudioCombo.SelectedIndex = 0;
    }

    private async Task LoadVisualizerAudioDevices()
    {
        List<(string Id, string Name)> rawDevices;
        try
        {
            rawDevices = await System.Threading.Tasks.Task.Run(() =>
            {
                var found = new List<(string, string)>();
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    found.Add((device.ID, device.FriendlyName));
                    device.Dispose();
                }
                return found;
            });
        }
        catch
        {
            rawDevices = new List<(string, string)>();
        }

        var devices = new List<AudioDeviceItem>
        {
            new() { Id = "", Name = Loc.Get("settings.visualizerAudio.default") }
        };

        foreach (var (id, name) in rawDevices)
        {
            devices.Add(new AudioDeviceItem { Id = id, Name = name });
        }

        if (!string.IsNullOrWhiteSpace(_settings.VisualizerAudioDeviceId) &&
            devices.All(d => d.Id != _settings.VisualizerAudioDeviceId))
        {
            devices.Add(new AudioDeviceItem
            {
                Id = _settings.VisualizerAudioDeviceId,
                Name = Loc.Get("settings.visualizerAudio.unavailable", _settings.VisualizerAudioDeviceId)
            });
        }

        VisualizerAudioCombo.ItemsSource = devices;
        VisualizerAudioCombo.DisplayMemberPath = "Name";

        var selectedIdx = devices.FindIndex(d => d.Id == _settings.VisualizerAudioDeviceId);
        VisualizerAudioCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;
    }

    private void VisualizerAudioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VisualizerAudioCombo.SelectedItem is AudioDeviceItem item)
        {
            _settings.VisualizerAudioDeviceId = item.Id;
        }
    }

    #endregion
}
