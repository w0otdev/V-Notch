using System.Windows;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Contracts;

public interface INotchManager : IDisposable
{
    NotchTransitionCoordinator TransitionCoordinator { get; }
    HoverDetectionService HoverService { get; }
    Rect SafeArea { get; }

    event EventHandler<Rect>? SafeAreaChanged;
    event EventHandler? PositionUpdated;

    void UpdateSettings(NotchSettings settings);
    void UpdatePosition();

    void Expand(NotchExpandMode mode = NotchExpandMode.Compact);
    void Collapse();
    void Hide();
    void Show();
}
