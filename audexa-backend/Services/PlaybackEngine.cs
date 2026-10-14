using audexa_backend.Data;
using audexa_backend.DTOs.Playback;
using audexa_backend.Models;
using Microsoft.EntityFrameworkCore;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Globalization;

namespace audexa_backend.Services;

public sealed class PlaybackEngine : IPlaybackEngine, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAudioDeviceService _devices;
    private readonly object _sync = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private PlaybackStateDto _state = new("idle", null, null, 0, 0, null, null, [], null);

    public PlaybackEngine(IServiceScopeFactory scopeFactory, IAudioDeviceService devices)
    {
        _scopeFactory = scopeFactory;
        _devices = devices;
    }

    public PlaybackStateDto GetState()
    {
        lock (_sync) return _state;
    }

    public async Task<PlaybackStateDto> StartScenarioAsync(Guid scenarioId, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await db.Scenarios.AsNoTracking()
            .Include(x => x.Steps)
            .ThenInclude(x => x.Room)
            .Include(x => x.Steps)
            .ThenInclude(x => x.AudioFile)
            .FirstOrDefaultAsync(x => x.Id == scenarioId, cancellationToken);

        if (scenario is null)
            throw new PlaybackException("SCENARIO_NOT_FOUND", "Scenario was not found.");
        if (scenario.Steps.Count == 0)
            throw new PlaybackException("SCENARIO_EMPTY", "Scenario has no steps.");

        var settings = await db.AudioConfigurations.AsNoTracking()
            .Include(x => x.Mappings)
            .SingleOrDefaultAsync(cancellationToken);
        if (settings?.DeviceId is null)
            throw new PlaybackException("DEVICE_NOT_CONFIGURED", "No audio device is configured.");

        var device = _devices.FindDevice(settings.DeviceId);
        if (device is null || !string.Equals(device.Status, "online", StringComparison.OrdinalIgnoreCase))
            throw new PlaybackException("DEVICE_UNAVAILABLE", "Configured audio device is unavailable.");

        var mappings = settings.Mappings.ToDictionary(x => x.RoomId, x => x.OutputId);
        foreach (var step in scenario.Steps)
        {
            if (!mappings.TryGetValue(step.RoomId, out var outputId) || string.IsNullOrWhiteSpace(outputId))
                throw new PlaybackException("OUTPUT_NOT_MAPPED", $"Room '{step.Room.Name}' has no audio output mapping.");
            if (!_devices.HasOutput(settings.DeviceId, outputId))
                throw new PlaybackException("OUTPUT_UNAVAILABLE", $"Output '{outputId}' is not available.");

            var path = GetAudioPath(step.AudioFile.StorageFileName);
            if (!File.Exists(path))
                throw new PlaybackException("AUDIO_FILE_MISSING", $"Audio file '{step.AudioFile.FileName}' is missing.");
        }

        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _runCts = cts;
            _state = new PlaybackStateDto(
                "starting", scenario.Id, scenario.Name, 0, scenario.Steps.Count,
                DateTime.UtcNow, null, [], null);
        }

        _runTask = Task.Run(() => RunScenarioAsync(scenario, settings.DeviceId, mappings, cts.Token), CancellationToken.None);
        return GetState();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        lock (_sync) cts = _runCts;
        if (cts is null) return;
        cts.Cancel();
        var runTask = _runTask;
        if (runTask is not null)
        {
            try { await runTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    public Task StopRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        // The first runtime version is scenario-atomic: stopping one room stops the current run.
        return StopAsync(cancellationToken);
    }

    private async Task RunScenarioAsync(Scenario scenario, string deviceId, Dictionary<Guid, string> mappings, CancellationToken cancellationToken)
    {
        try
        {
            SetState("playing", scenario, 0, []);
            do
            {
                var steps = scenario.Steps.OrderBy(x => x.Order).ToList();
                for (var i = 0; i < steps.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var step = steps[i];
                    var outputId = mappings[step.RoomId];
                    var roomState = new PlaybackRoomStateDto(step.RoomId, "playing", scenario.Id, scenario.Name,
                        step.AudioFileId, step.AudioFile.FileName, outputId, i + 1, steps.Count, 0,
                        step.AudioFile.Duration.TotalSeconds, null);
                    SetState("playing", scenario, i + 1, [roomState]);
                    await PlayFileAsync(deviceId, outputId, step.AudioFile, step.Room.Volume, scenario, step, i + 1, steps.Count, cancellationToken);

                    if (step.DelayMs > 0)
                    {
                        SetState("waiting", scenario, i + 1, [roomState with { Status = "waiting", PositionSeconds = step.AudioFile.Duration.TotalSeconds }]);
                        await Task.Delay(step.DelayMs, cancellationToken);
                    }
                }
            } while (string.Equals(scenario.Repeat, "loop", StringComparison.OrdinalIgnoreCase));

            SetState("idle", scenario, scenario.Steps.Count, []);
        }
        catch (OperationCanceledException)
        {
            SetState("stopped", scenario, GetState().CurrentStep, []);
        }
        catch (Exception ex)
        {
            SetState("error", scenario, GetState().CurrentStep, [], ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                _runCts?.Dispose();
                _runCts = null;
                _runTask = null;
            }
        }
    }

    private void SetState(string status, Scenario scenario, int step, IReadOnlyList<PlaybackRoomStateDto> rooms, string? error = null)
    {
        lock (_sync)
        {
            _state = new PlaybackStateDto(status, scenario.Id, scenario.Name, step, scenario.Steps.Count,
                _state.StartedAt ?? DateTime.UtcNow,
                status is "idle" or "stopped" or "error" ? DateTime.UtcNow : null,
                rooms, error);
        }
    }

    private static string GetAudioPath(string storageFileName)
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataPath, "Audexa", "Audio", storageFileName);
    }

    private async Task PlayFileAsync(string deviceId, string outputId, AudioFile audio, int roomVolume, Scenario scenario, ScenarioStep step, int stepNumber, int totalSteps, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlaybackException("UNSUPPORTED_PLATFORM", "Audio playback is supported on Windows only.");

        var path = GetAudioPath(audio.StorageFileName);
        using var source = OpenSource(path);
        var output = CreateOutput(deviceId, outputId, out var targetFormat, out var targetChannel);
        var gain = Math.Clamp(roomVolume / 100d, 0d, 1d) * Math.Pow(10d, audio.VolumeDb / 20d);
        using var provider = new RoutedWaveProvider(source, targetFormat, targetChannel, Math.Clamp(gain, 0d, 4d));
        output.Init(provider);
        output.Play();
        var started = DateTime.UtcNow;
        var duration = Math.Max(0.001, audio.Duration.TotalSeconds);

        try
        {
            while (output.PlaybackState == PlaybackState.Playing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var position = Math.Min(duration, (DateTime.UtcNow - started).TotalSeconds);
                SetState(
                    "playing",
                    scenario,
                    stepNumber,
                    [new PlaybackRoomStateDto(
                        step.RoomId,
                        "playing",
                        scenario.Id,
                        scenario.Name,
                        step.AudioFileId,
                        audio.FileName,
                        outputId,
                        stepNumber,
                        totalSteps,
                        position,
                        duration,
                        null)]);
                await Task.Delay(100, cancellationToken);
            }
        }
        finally
        {
            output.Stop();
            output.Dispose();
        }
    }

    private static WaveStream OpenSource(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".wav" => new WaveFileReader(path),
            ".mp3" => new Mp3FileReader(path),
            _ => throw new PlaybackException("UNSUPPORTED_AUDIO", $"Unsupported audio format '{ext}'.")
        };
    }

    private static IWavePlayer CreateOutput(string deviceId, string outputId, out WaveFormat targetFormat, out int targetChannel)
    {
        targetChannel = ParseOutputIndex(outputId);

        if (deviceId.StartsWith("asio:", StringComparison.Ordinal))
        {
            var driverName = deviceId[5..];
            using var probe = new AsioOut(driverName);
            var channels = Math.Max(probe.DriverOutputChannelCount, targetChannel + 1);
            targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
            return new AsioOut(driverName);
        }

        if (deviceId.StartsWith("wasapi:render:", StringComparison.Ordinal))
        {
            var endpointId = deviceId["wasapi:render:".Length..];
            using var enumerator = new MMDeviceEnumerator();
            var endpoint = enumerator.GetDevice(endpointId);
            var mixFormat = endpoint.AudioClient.MixFormat;
            targetFormat = new WaveFormat(mixFormat.SampleRate, 16, mixFormat.Channels);
            return new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 100);
        }

        if (deviceId.StartsWith("waveout:", StringComparison.Ordinal) &&
            int.TryParse(deviceId["waveout:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var deviceNumber))
        {
            targetFormat = new WaveFormat(48000, 16, Math.Max(2, targetChannel + 1));
            return new WaveOutEvent { DeviceNumber = deviceNumber, DesiredLatency = 100 };
        }

        throw new PlaybackException("UNSUPPORTED_DEVICE", $"Unsupported device '{deviceId}'.");
    }

    private static int ParseOutputIndex(string outputId)
    {
        var marker = outputId.LastIndexOf(":out:", StringComparison.Ordinal);
        if (marker >= 0 && int.TryParse(outputId[(marker + 5)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var asioIndex))
            return asioIndex;
        var outputMarker = outputId.LastIndexOf(":output:", StringComparison.Ordinal);
        if (outputMarker >= 0 && int.TryParse(outputId[(outputMarker + 8)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var wasapiIndex))
            return wasapiIndex;
        var genericMarker = outputId.LastIndexOf(":output:", StringComparison.Ordinal);
        if (genericMarker >= 0 && int.TryParse(outputId[(genericMarker + 8)..], out var index)) return index;
        throw new PlaybackException("INVALID_OUTPUT", $"Cannot determine output channel from '{outputId}'.");
    }

    private sealed class RoutedWaveProvider : IWaveProvider, IDisposable
    {
        private readonly WaveStream _source;
        private readonly WaveFormat _target;
        private readonly int _channel;
        private readonly float _volume;
        private readonly MediaFoundationResampler? _resampler;
        private readonly IWaveProvider _input;

        public RoutedWaveProvider(WaveStream source, WaveFormat target, int channel, double volume)
        {
            _source = source;
            _target = target;
            _channel = Math.Clamp(channel, 0, Math.Max(0, target.Channels - 1));
            _volume = (float)volume;
            if (source.WaveFormat.SampleRate != target.SampleRate || source.WaveFormat.Channels != 1 || source.WaveFormat.BitsPerSample != 16)
            {
                _resampler = new MediaFoundationResampler(source, new WaveFormat(target.SampleRate, 16, 1));
                _resampler.ResamplerQuality = 60;
                _input = _resampler;
            }
            else _input = source;
        }

        public WaveFormat WaveFormat => _target;

        public int Read(byte[] buffer, int offset, int count)
        {
            var bytesPerFrame = _target.BlockAlign;
            var frames = count / bytesPerFrame;
            if (frames <= 0) return 0;
            var sourceBuffer = new byte[frames * 2];
            var read = _input.Read(sourceBuffer, 0, sourceBuffer.Length);
            var sourceFrames = read / 2;
            if (sourceFrames == 0) return 0;
            var bytesWritten = sourceFrames * bytesPerFrame;
            Array.Clear(buffer, offset, bytesWritten);
            for (var frame = 0; frame < sourceFrames; frame++)
            {
                var sample = BitConverter.ToInt16(sourceBuffer, frame * 2) / 32768f * _volume;
                var sampleOffset = offset + frame * bytesPerFrame + _channel * (_target.BitsPerSample / 8);
                if (_target.Encoding == WaveFormatEncoding.IeeeFloat && _target.BitsPerSample == 32)
                    BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), sample);
                else if (_target.Encoding == WaveFormatEncoding.Pcm && _target.BitsPerSample == 16)
                    BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 2), (short)(sample * short.MaxValue));
                else if (_target.Encoding == WaveFormatEncoding.Pcm && _target.BitsPerSample == 32)
                    BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), (int)(sample * int.MaxValue));
                else throw new PlaybackException("UNSUPPORTED_FORMAT", $"Unsupported target format { _target }.");
            }
            return bytesWritten;
        }

        public void Dispose() => _resampler?.Dispose();
    }

    public void Dispose()
    {
        lock (_sync) _runCts?.Cancel();
    }
}

public sealed class PlaybackException : Exception
{
    public PlaybackException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}
