using System.Buffers.Binary;
using System.Windows.Interop;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class InputMonitorServiceTests
{
    [Fact]
    public void RegistrationCanStopAndRestartWithoutShowingAWindow() => SharedStaTestRunner.Run(() =>
    {
        using var source = new HwndSource(new HwndSourceParameters("VNotch raw input test")
        { Width = 1, Height = 1, WindowStyle = 0 });
        try
        {
            InputMonitorService.Start(source.Handle);
            Assert.True(InputMonitorService.IsStarted);
            InputMonitorService.Start(source.Handle);
            InputMonitorService.Stop();
            Assert.False(InputMonitorService.IsStarted);
            InputMonitorService.Stop();
            InputMonitorService.Start(source.Handle);
            Assert.True(InputMonitorService.IsStarted);
        }
        finally { InputMonitorService.Stop(); }
    });

    [Theory]
    [InlineData(0, false)] // movement
    [InlineData(1, true)]
    [InlineData(2, false)] // release
    [InlineData(4, false)] // right button
    [InlineData(0x400, false)] // wheel
    [InlineData(0x401, true)]
    public void OnlyLeftButtonPressPacketsTriggerAnAction(ushort flags, bool expected)
    {
        var packet = MousePacket(flags);
        Assert.Equal(expected, InputMonitorService.IsLeftButtonDown(packet));
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 1); // keyboard header
        Assert.False(InputMonitorService.IsLeftButtonDown(packet));
    }

    [Fact]
    public void TruncatedOrInvalidPacketsAreIgnored()
    {
        var packet = MousePacket(1);
        for (int length = 0; length < packet.Length; length++)
            Assert.False(InputMonitorService.IsLeftButtonDown(packet.AsSpan(0, length)));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), uint.MaxValue);
        Assert.False(InputMonitorService.IsLeftButtonDown(packet));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 8);
        Assert.False(InputMonitorService.IsLeftButtonDown(packet));
    }

    [Theory]
    [InlineData(-1920, -1080)]
    [InlineData(2560, 1440)]
    [InlineData(-32768, 32767)]
    public void QueuedMessagePositionPreservesNegativeMonitorCoordinates(int x, int y)
    {
        uint packed = (uint)(ushort)x | ((uint)(ushort)y << 16);
        var point = InputMonitorService.DecodeMessagePosition(packed);
        Assert.Equal(x, point.x);
        Assert.Equal(y, point.y);
    }

    private static byte[] MousePacket(ushort flags)
    {
        int headerSize = 8 + 2 * IntPtr.Size;
        var packet = new byte[headerSize + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(headerSize + 4), flags);
        return packet;
    }
}
