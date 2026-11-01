using TrayX.Windows.Audio;

namespace TrayX.IntegrationTests;

public sealed class AudioEndpointIntegrationTests
{
    [Fact]
    public void DeviceNotifications_CanBeRegisteredAndReleased()
    {
        var service = new AudioCenterService();

        service.StartDeviceNotifications();
        _ = service.GetState();
        service.StopDeviceNotifications();
    }

    [Fact]
    public void GetState_ReportsRealEndpointsAndControlAvailability()
    {
        var state = new AudioCenterService().GetState();

        Assert.All(state.OutputDevices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.Equal("render", device.Flow);
            Assert.Equal(device.CanControl, device.Volume.HasValue);
        });

        Assert.All(state.InputDevices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.Equal("capture", device.Flow);
            Assert.Equal(device.CanControl, device.Volume.HasValue);
        });

        if (state.HasDevice)
        {
            Assert.Contains(state.OutputDevices, device => device.IsDefault);
            Assert.Equal(
                state.DeviceId,
                state.OutputDevices.Single(device => device.IsDefault).Id);
        }
    }

    [Fact]
    public void GetDefaultInputPeak_IsNullOrWithinMeterRange()
    {
        var peak = new AudioCenterService().GetDefaultInputPeak();

        if (peak is not null)
        {
            Assert.InRange(peak.Level, 0, 1);
        }
    }

    [Fact]
    public void GetDefaultDevice_ReturnsAnEndpointIdentityWhenAvailable()
    {
        var service = new AudioCenterService();
        var device = service.GetDefaultDevice();

        if (!string.IsNullOrWhiteSpace(device.Id))
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
            Assert.Contains(
                service.GetState().OutputDevices,
                endpoint => string.Equals(endpoint.Id, device.Id, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Empty(device.Name);
        }
    }
}
