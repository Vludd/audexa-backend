using Microsoft.AspNetCore.Mvc;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace audexa_backend.Controllers;

[ApiController]
[Route("api/audio-devices")]
public class AudioDevicesController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Ok(new AudioDevicesResponse(
                Supported: false,
                Devices: [],
                Error: "Audio device enumeration is supported on Windows only."
            ));
        }

        var devices = new List<AudioDeviceDto>();
        string? enumerationError = null;

        // Windows shared-mode audio devices: speakers, headphones, HDMI, USB audio, etc.
        // Prefer WASAPI/Core Audio because it gives us stable endpoint IDs.
        var wasapiRenderFound = false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(
                         DataFlow.Render,
                         DeviceState.All))
            {
                // Only expose devices that Windows considers present. Disabled and
                // unplugged endpoints are useful for diagnostics, but are not valid
                // playback targets yet.
                if (device.State != DeviceState.Active)
                    continue;

                try
                {
                    var outputCount = GetChannelCount(device);
                    var outputs = CreateGenericChannels(
                        device.ID,
                        outputCount,
                        "output",
                        "OUT");

                    devices.Add(new AudioDeviceDto(
                        Id: $"wasapi:render:{device.ID}",
                        Name: device.FriendlyName,
                        Type: "WASAPI",
                        Driver: "Windows Audio",
                        Status: "online",
                        OutputCount: outputCount,
                        InputCount: 0,
                        Outputs: outputs,
                        Inputs: []
                    ));

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

        // Fallback for Windows machines where Core Audio enumeration is unavailable
        // from the current process/session. WinMM still exposes normal playback
        // devices such as laptop speakers and headphones.
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
                        Id: deviceId,
                        Name: capabilities.ProductName,
                        Type: "WaveOut",
                        Driver: "Windows Multimedia",
                        Status: "online",
                        OutputCount: outputCount,
                        InputCount: 0,
                        Outputs: CreateGenericChannels(
                            deviceId,
                            outputCount,
                            "output",
                            "OUT"),
                        Inputs: []
                    ));
                }
            }
            catch (Exception ex)
            {
                enumerationError ??= $"WinMM fallback enumeration failed: {ex.Message}";
            }
        }

        // Windows capture endpoints are also exposed because they will be useful
        // later for microphone/input diagnostics.
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(
                         DataFlow.Capture,
                         DeviceState.All))
            {
                if (device.State != DeviceState.Active)
                    continue;

                try
                {
                    var inputCount = GetChannelCount(device);
                    var inputs = CreateGenericChannels(
                        device.ID,
                        inputCount,
                        "input",
                        "IN");

                    devices.Add(new AudioDeviceDto(
                        Id: $"wasapi:capture:{device.ID}",
                        Name: device.FriendlyName,
                        Type: "WASAPI",
                        Driver: "Windows Audio",
                        Status: "online",
                        OutputCount: 0,
                        InputCount: inputCount,
                        Outputs: [],
                        Inputs: inputs
                    ));
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

        // ASIO remains available for real multichannel interfaces.
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
                            Id: $"{driverName}:out:{index}",
                            Index: index,
                            Name: SafeGetOutputChannelName(asio, index),
                            Direction: "output"
                        ))
                        .ToArray();

                    var inputs = Enumerable
                        .Range(0, asio.DriverInputChannelCount)
                        .Select(index => new AudioChannelDto(
                            Id: $"{driverName}:in:{index}",
                            Index: index,
                            Name: SafeGetInputChannelName(asio, index),
                            Direction: "input"
                        ))
                        .ToArray();

                    devices.Add(new AudioDeviceDto(
                        Id: $"asio:{driverName}",
                        Name: driverName,
                        Type: "ASIO",
                        Driver: driverName,
                        Status: "online",
                        OutputCount: outputs.Length,
                        InputCount: inputs.Length,
                        Outputs: outputs,
                        Inputs: inputs
                    ));
                }
                catch (Exception ex)
                {
                    devices.Add(new AudioDeviceDto(
                        Id: $"asio:{driverName}",
                        Name: driverName,
                        Type: "ASIO",
                        Driver: driverName,
                        Status: "offline",
                        OutputCount: 0,
                        InputCount: 0,
                        Outputs: [],
                        Inputs: []
                    ));

                    enumerationError ??= ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            enumerationError ??= ex.Message;
        }

        return Ok(new AudioDevicesResponse(
            Supported: true,
            Devices: devices,
            Error: enumerationError
        ));
    }

    private static int GetChannelCount(MMDevice device)
    {
        try
        {
            return Math.Max(1, device.AudioClient.MixFormat.Channels);
        }
        catch
        {
            // A normal Windows playback endpoint is at least stereo in the
            // configurations we expect, but do not let metadata failures hide it.
            return 2;
        }
    }

    private static AudioChannelDto[] CreateGenericChannels(
        string deviceId,
        int count,
        string direction,
        string prefix)
    {
        return Enumerable
            .Range(0, count)
            .Select(index => new AudioChannelDto(
                Id: $"{deviceId}:{direction}:{index}",
                Index: index,
                Name: $"{prefix} {index + 1:00}",
                Direction: direction
            ))
            .ToArray();
    }

    private static string SafeGetOutputChannelName(AsioOut asio, int index)
    {
        try
        {
            var name = asio.AsioOutputChannelName(index);
            return string.IsNullOrWhiteSpace(name)
                ? $"OUT {index + 1:00}"
                : name;
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
            return string.IsNullOrWhiteSpace(name)
                ? $"IN {index + 1:00}"
                : name;
        }
        catch
        {
            return $"IN {index + 1:00}";
        }
    }

    private sealed record AudioDevicesResponse(
        bool Supported,
        IReadOnlyList<AudioDeviceDto> Devices,
        string? Error);

    private sealed record AudioDeviceDto(
        string Id,
        string Name,
        string Type,
        string Driver,
        string Status,
        int OutputCount,
        int InputCount,
        IReadOnlyList<AudioChannelDto> Outputs,
        IReadOnlyList<AudioChannelDto> Inputs);

    private sealed record AudioChannelDto(
        string Id,
        int Index,
        string Name,
        string Direction);
}
