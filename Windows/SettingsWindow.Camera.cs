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
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    #region Camera Device

    private async Task LoadCameraDevices()
    {
        try
        {
            var cameras = await Task.Run(async () =>
            {
                var groups = await global::Windows.Media.Capture.Frames.MediaFrameSourceGroup.FindAllAsync();
                var list = groups
                    .Where(g => g.SourceInfos.Any(s => s.SourceKind == global::Windows.Media.Capture.Frames.MediaFrameSourceKind.Color))
                    .Select(g => new CameraDeviceItem { Id = g.Id, Name = g.DisplayName })
                    .ToList();

                if (list.Count == 0)
                {
                    list.Add(new CameraDeviceItem { Id = "", Name = Loc.Get("settings.camera.none") });
                }
                return list;
            }, CancellationToken.None);

            if (_isClosing || Dispatcher.HasShutdownStarted) return;

            CameraCombo.ItemsSource = cameras;
            CameraCombo.DisplayMemberPath = "Name";

            var selectedIdx = cameras.FindIndex(c => c.Id == _settings.CameraDeviceId);
            CameraCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;
        }
        catch
        {
            if (_isClosing || Dispatcher.HasShutdownStarted) return;
            CameraCombo.ItemsSource = new[] { new CameraDeviceItem { Id = "", Name = Loc.Get("settings.camera.none") } };
            CameraCombo.DisplayMemberPath = "Name";
            CameraCombo.SelectedIndex = 0;
        }
    }

    private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CameraCombo.SelectedItem is CameraDeviceItem item)
        {
            _settings.CameraDeviceId = item.Id;
            if (!_isLoadingSettings) PushLivePreview();
        }
    }

    #endregion
}
