using System.Threading;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class GpuMonitorServiceTests
{
    [Fact]
    public void MetricReads_DoNotIncrementConsumerCount()
    {
        var service = GpuMonitorService.Instance;
        int initialConsumers = service.ConsumerCount;

        // Reading metrics repeatedly must never increment consumer count
        for (int i = 0; i < 50; i++)
        {
            var usage = service.GetGpuUsage();
            _ = usage.ProcessGpuPercent;
            _ = usage.GlobalGpuPercent;

            var snapshot = service.SampleFastMetrics(60.0, 60);
            _ = snapshot.Fps;
        }

        Assert.Equal(initialConsumers, service.ConsumerCount);
    }

    [Fact]
    public void StartAndStop_ProperlyBalancesConsumerCount()
    {
        var service = GpuMonitorService.Instance;
        int initialConsumers = service.ConsumerCount;

        service.Start();
        Assert.Equal(initialConsumers + 1, service.ConsumerCount);
        Assert.True(service.IsRunning);

        service.Start();
        Assert.Equal(initialConsumers + 2, service.ConsumerCount);

        service.Stop();
        Assert.Equal(initialConsumers + 1, service.ConsumerCount);

        service.Stop();
        Assert.Equal(initialConsumers, service.ConsumerCount);
    }

    [Fact]
    public void EnsureSamplerRunning_IsIdempotent()
    {
        var service = GpuMonitorService.Instance;

        // Ensure starting from 0 consumers if possible
        while (service.ConsumerCount > 0)
        {
            service.Stop();
        }

        Assert.Equal(0, service.ConsumerCount);
        Assert.False(service.IsRunning);

        // First call sets consumer count to 1 and starts runner
        service.EnsureSamplerRunning();
        Assert.Equal(1, service.ConsumerCount);
        Assert.True(service.IsRunning);

        // Subsequent calls do not inflate consumer count
        for (int i = 0; i < 10; i++)
        {
            service.EnsureSamplerRunning();
        }

        Assert.Equal(1, service.ConsumerCount);

        // One stop brings it back to 0
        service.Stop();
        Assert.Equal(0, service.ConsumerCount);
        Assert.False(service.IsRunning);
    }
}
