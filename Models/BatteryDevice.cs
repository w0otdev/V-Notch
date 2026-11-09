namespace VNotch.Models;

public sealed record BatteryDevice(string Id, string Name, int Percentage, bool IsConnected)
{
    public bool HasReading => IsConnected && Percentage is >= 0 and <= 100;
}
