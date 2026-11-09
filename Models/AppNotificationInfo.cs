using System;
using System.Windows.Media;

namespace VNotch.Models;

public sealed class AppNotificationInfo
{
    public uint Id { get; init; }
    public string AppId { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public ImageSource? AppIcon { get; init; }
    public DateTimeOffset CreationTime { get; init; } = DateTimeOffset.UtcNow;
}
