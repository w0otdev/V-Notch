using VNotch.Models;

namespace VNotch.Services;

public interface IBatteryService
{

    BatteryInfo GetBatteryInfo();

    Task<BatteryInfo> GetBatteryInfoAsync(string deviceId) => Task.FromResult(GetBatteryInfo());
}
