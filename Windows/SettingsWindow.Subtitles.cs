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
    #region Subtitle Priority

    private sealed class SubtitlePriorityItem
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";

        public override string ToString() => DisplayName;
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<SubtitlePriorityItem> _subtitleItems = new();
    private Point _subtitleDragStart;
    private bool _subtitleIsDragging;

    private void LoadSubtitlePriority()
    {
        _subtitleItems.Clear();

        var keys = (_settings.SubtitlePriority ?? "native,english,auto")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var allKeys = new[] { "native", "english", "auto" };
        var ordered = keys.Where(allKeys.Contains).Distinct(StringComparer.Ordinal).ToList();
        ordered.AddRange(allKeys.Where(k => !ordered.Contains(k)));

        foreach (var key in ordered)
        {
            _subtitleItems.Add(new SubtitlePriorityItem
            {
                Key = key,
                DisplayName = GetSubtitleModeName(key)
            });
        }

        SubtitlePriorityItems.ItemsSource = _subtitleItems;
    }

    private static string GetSubtitleModeName(string key) => key switch
    {
        "native" => Loc.Get("settings.subtitleMode.native"),
        "english" => Loc.Get("settings.subtitleMode.english"),
        "auto" => Loc.Get("settings.subtitleMode.auto"),
        _ => key
    };

    private void SubtitlePriorityItem_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _subtitleDragStart = e.GetPosition(null);
        _subtitleIsDragging = false;
        e.Handled = true;
    }

    private void SubtitlePriorityItem_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (_subtitleIsDragging) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _subtitleDragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - _subtitleDragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _subtitleIsDragging = true;

            if (sender is FrameworkElement fe && fe.DataContext is SubtitlePriorityItem item)
            {
                var data = new DataObject(SubtitlePriorityDataFormat, item);
                DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
            }

            _subtitleIsDragging = false;
        }
    }

    private void SubtitlePriorityItem_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void SubtitlePriority_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SubtitlePriorityDataFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SubtitlePriority_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SubtitlePriorityDataFormat)) return;

        var draggedItem = e.Data.GetData(SubtitlePriorityDataFormat) as SubtitlePriorityItem;
        if (draggedItem == null) return;

        var dropPos = e.GetPosition(SubtitlePriorityItems);
        int newIndex = GetSubtitleDropIndex(dropPos);

        int oldIndex = _subtitleItems.IndexOf(draggedItem);
        if (oldIndex < 0 || oldIndex == newIndex) return;

        var positions = new Dictionary<SubtitlePriorityItem, double>();
        for (int i = 0; i < _subtitleItems.Count; i++)
        {
            var container = SubtitlePriorityItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container != null)
                positions[_subtitleItems[i]] = container.TranslatePoint(new Point(0, 0), SubtitlePriorityItems).Y;
        }

        _subtitleItems.Move(oldIndex, newIndex);

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            AnimateSubtitlePriorityReorder(draggedItem, positions);
        });

        ApplySettingsFromUi(persist: true);
    }

    private void AnimateSubtitlePriorityReorder(SubtitlePriorityItem draggedItem, Dictionary<SubtitlePriorityItem, double> positions)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < _subtitleItems.Count; i++)
        {
            var container = SubtitlePriorityItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            var item = _subtitleItems[i];
            double newY = container.TranslatePoint(new Point(0, 0), SubtitlePriorityItems).Y;

            if (positions.TryGetValue(item, out double oldY) && Math.Abs(oldY - newY) > 1)
            {
                var translate = container.RenderTransform as TranslateTransform;
                if (translate == null)
                {
                    translate = new TranslateTransform();
                    container.RenderTransform = translate;
                }

                translate.Y = oldY - newY;
                var anim = new DoubleAnimation(oldY - newY, 0, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = ease
                };
                Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
                translate.BeginAnimation(TranslateTransform.YProperty, anim);
            }

            if (item == draggedItem && container.RenderTransform is TranslateTransform)
            {
                var group = new TransformGroup();
                group.Children.Add(container.RenderTransform);
                var sc = new ScaleTransform(1, 1);
                group.Children.Add(sc);
                container.RenderTransformOrigin = new Point(0.5, 0.5);
                container.RenderTransform = group;

                var scaleAnim = new DoubleAnimation(1.03, 1.0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = ease
                };
                Timeline.SetDesiredFrameRate(scaleAnim, AnimationConfig.TargetFps);
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            }
        }
    }

    private int GetSubtitleDropIndex(Point dropPoint)
    {
        double y = 0;
        for (int i = 0; i < _subtitleItems.Count; i++)
        {
            var container = SubtitlePriorityItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            double itemHeight = container.ActualHeight;
            if (dropPoint.Y < y + itemHeight / 2)
                return i;
            y += itemHeight;
        }
        return _subtitleItems.Count - 1;
    }

    private string GetSubtitlePriorityString()
    {
        return string.Join(",", _subtitleItems.Select(i => i.Key));
    }

    #endregion
}
