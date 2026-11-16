using VNotch.Contracts;

namespace VNotch.Services;

public sealed class UiModuleScheduler : IModuleScheduler
{
    public static UiModuleScheduler Instance { get; } = new();
    public IModuleTimer CreateTimer() => new UiScheduledTimer();
}
