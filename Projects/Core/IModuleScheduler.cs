namespace VNotch.Contracts;

public interface IModuleTimer : IDisposable
{
    TimeSpan Interval { get; set; }
    event EventHandler? Tick;
    void Start();
    void Stop();
}

public interface IModuleScheduler
{
    IModuleTimer CreateTimer();
}
