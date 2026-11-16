using System;

namespace VNotch.Contracts;

public interface INotchModule : IDisposable
{

    string ModuleName { get; }

    TimeSpan? TickInterval { get; }

    bool IsRunning { get; }

    void Initialize();

    void Start();

    void Stop();

    void Tick();
}
