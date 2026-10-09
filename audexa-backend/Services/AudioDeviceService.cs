using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace audexa_backend.Services;

public sealed class AudioDeviceService : IAudioDeviceService
{
    public AudioDevicesResponse GetDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AudioDevicesResponse(
                Supported: false,
                Devices: [],
                Error: "Audio device enumeration is supported on Windows only.");
        }

        var devices = new List<AudioDeviceDto>();
        string? enumerationError = null;
        var wasapiRenderFound = false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
            {
                if (device.State != DeviceState.Active)
                    continue;

                try
                {
                    var outputCount = GetChannelCount(device);
                    var outputs = CreateGenericChannels(device.ID, outputCount, "output", "OUT");

                    devices.Add(new AudioDeviceDto(
                        $"wasapi:render:{device.ID}",
                        device.FriendlyName,
                        "WASAPI",
                        "Windows Audio",
                        "online",
                        outputCount,
                        0,
                        outputs,
                        []));

                    wasapiRenderFound = true;
                }
                catch (Exception ex)
                {
                    enumerationError ??= $"WASAPI device '{device.ID}' could not be read: {ex.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            enumerationError ??= $"WASAPI render enumeration failed: {ex.Message}";
        }

        if (!wasapiRenderFound)
        {
            try
            {
                var deviceCount = WaveInterop.waveOutGetNumDevs();
                for (var index = 0; index < deviceCount; index++)
                {
                    var result = WaveInterop.waveOutGetDevCaps(
                        new IntPtr(index),
                        out var capabilities,
                        Marshal.SizeOf<WaveOutCapabilities>());

                    if (result != NAudio.MmResult.NoError)
                    {
                        throw new InvalidOperationException(
                            $"Could not read WinMM device {index}: {result}.");
                    }

                    var outputCount = Math.Max(1, capabilities.Channels);
                    var deviceId = $"waveout:{index}";

                    devices.Add(new AudioDeviceDto(
                        deviceId,
                        capabilities.ProductName,
                        "WaveOut",
                        "Windows Multimedia",
                        "online",
                        outputCount,
                        0,
                        CreateGenericChannels(deviceId, outputCount, "output", "OUT"),
                        []));
                }
            }
            catch (Exception ex)
            {
                enumerationError ??= $"WinMM fallback enumeration failed: {ex.Message}";
            }
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All))
            {
                if (device.State != DeviceState.Active)
                    continue;

                try
                {
                    var inputCount = GetChannelCount(device);
                    var inputs = CreateGenericChannels(device.ID, inputCount, "input", "IN");

                    devices.Add(new AudioDeviceDto(
                        $"wasapi:capture:{device.ID}",
                        device.FriendlyName,
                        "WASAPI",
                        "Windows Audio",
                        "online",
                        0,
                        inputCount,
                        [],
                        inputs));
                }
                catch (Exception ex)
                {
                    enumerationError ??= $"WASAPI capture device '{device.ID}' could not be read: {ex.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            enumerationError ??= $"WASAPI capture enumeration failed: {ex.Message}";
        }

        try
        {
            foreach (var driverName in AsioOut.GetDriverNames())
            {
                try
                {
                    using var asio = new AsioOut(driverName);

                    var outputs = Enumerable
                        .Range(0, asio.DriverOutputChannelCount)
                        .Select(index => new AudioChannelDto(
                            $"{driverName}:out:{index}",
                            index,
                            SafeGetOutputChannelName(asio, index),
                            "output"))
                        .ToArray();

                    var inputs = Enumerable
                        .Range(0, asio.DriverInputChannelCount)
                        .Select(index => new AudioChannelDto(
                            $"{driverName}:in:{index}",
                            index,
                            SafeGetInputChannelName(asio, index),
                            "input"))
                        .ToArray();

                    devices.Add(new AudioDeviceDto(
                        $"asio:{driverName}",
                        driverName,
                        "ASIO",
                        driverName,
                        "online",
                        outputs.Length,
                        inputs.Length,
                        outputs,
                        inputs));
                }
                catch (Exception ex)
                {
                    devices.Add(new AudioDeviceDto(
                        $"asio:{driverName}",
                        driverName,
                        "ASIO",
                        driverName,
                        "offline",
                        0,
                        0,
                        [],
                        []));

                    enumerationError ??= ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            enumerationError ??= ex.Message;
        }

        return new AudioDevicesResponse(true, devices, enumerationError);
    }

    public AudioDeviceDto? FindDevice(string deviceId)
    {
        return GetDevices().Devices.FirstOrDefault(device => device.Id == deviceId);
    }

    public bool HasOutput(string deviceId, string outputId)
    {
        var device = FindDevice(deviceId);
        return device?.Outputs.Any(output => output.Id == outputId) == true;
    }

    private static int GetChannelCount(MMDevice device)
    {
        try
        {
            return Math.Max(1, device.AudioClient.MixFormat.Channels);
        }
        catch
        {
            return 2;
        }
    }

    private static AudioChannelDto[] CreateGenericChannels(
        string deviceId,
        int count,
        string direction,
        string prefix)
    {
        return Enumerable.Range(0, count)
            .Select(index => new AudioChannelDto(
                $"{deviceId}:{direction}:{index}",
                index,
                $"{prefix} {index + 1:00}",
                direction))
            .ToArray();
    }

    private static string SafeGetOutputChannelName(AsioOut asio, int index)
    {
        try
        {
            var name = asio.AsioOutputChannelName(index);
            return string.IsNullOrWhiteSpace(name) ? $"OUT {index + 1:00}" : name;
        }
        catch
        {
            return $"OUT {index + 1:00}";
        }
    }

    private static string SafeGetInputChannelName(AsioOut asio, int index)
    {
        try
        {
            var name = asio.AsioInputChannelName(index);
            return string.IsNullOrWhiteSpace(name) ? $"IN {index + 1:00}" : name;
        }
        catch
        {
            return $"IN {index + 1:00}";
        }
    }
}
