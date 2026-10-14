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
    private PlaybackRuntime? _active;
    private PlaybackStateDto _state = new("idle", null, null, 0, null, 0, null, null, [], [], null);

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
            .Include(x => x.Steps).ThenInclude(x => x.Room)
            .Include(x => x.Steps).ThenInclude(x => x.AudioFile)
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
        var steps = scenario.Steps.OrderBy(x => x.Order).ToList();

        foreach (var step in steps)
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
        var stepStates = steps.Select((step, index) => new PlaybackStepStateDto(
            index + 1,
            step.RoomId,
            step.Room.Name,
            step.AudioFileId,
            step.AudioFile.FileName,
            index == 0 ? "pending" : "pending",
            step.DelayMs,
            0,
            0,
            Math.Max(0.001, step.AudioFile.Duration.TotalSeconds),
            mappings[step.RoomId],
            null)).ToList();

        lock (_sync)
        {
            _runCts = cts;
            _state = new PlaybackStateDto(
                "starting",
                scenario.Id,
                scenario.Name,
                0,
                steps.Count > 0 ? 1 : null,
                steps.Count,
                DateTime.UtcNow,
                null,
                [],
                stepStates,
                null);
        }

        _runTask = Task.Run(() => RunScenarioAsync(scenario, settings.DeviceId, mappings, cts.Token), CancellationToken.None);
        return GetState();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_sync)
        {
            cts = _runCts;
            task = _runTask;
        }

        if (cts is null) return;

        cts.Cancel();
        lock (_sync) _active?.Stop();

        if (task is not null)
        {
            try { await task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    public Task StopRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_active?.RoomId == roomId)
                _active.RequestSkip();
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_active is null || _state.Status != "playing") return Task.CompletedTask;
            _active.Pause();
            var current = _state.Steps.FirstOrDefault(x => x.Step == _state.CurrentStep);
            if (current is not null)
                SetStepState(current.Step, "paused", current.PositionSeconds, 0);
            else
                _state = _state with { Status = "paused" };
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_active is null || _state.Status != "paused") return Task.CompletedTask;
            _active.Resume();
            _state = _state with { Status = "playing" };
        }

        return Task.CompletedTask;
    }

    public async Task SetRoomVolumeAsync(Guid roomId, int volume, CancellationToken cancellationToken = default)
    {
        volume = Math.Clamp(volume, 0, 100);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var room = await db.Rooms.FirstOrDefaultAsync(x => x.Id == roomId, cancellationToken);
        if (room is null) throw new PlaybackException("ROOM_NOT_FOUND", "Room was not found.");

        room.Volume = volume;
        await db.SaveChangesAsync(cancellationToken);

        lock (_sync)
        {
            if (_active?.RoomId == roomId)
                _active.SetRoomVolume(volume);
        }
    }

    public async Task SetAudioGainAsync(Guid audioFileId, int gainDb, CancellationToken cancellationToken = default)
    {
        gainDb = Math.Clamp(gainDb, -24, 24);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audio = await db.AudioFiles.FirstOrDefaultAsync(x => x.Id == audioFileId, cancellationToken);
        if (audio is null) throw new PlaybackException("AUDIO_FILE_NOT_FOUND", "Audio file was not found.");

        audio.VolumeDb = gainDb;
        await db.SaveChangesAsync(cancellationToken);

        lock (_sync)
        {
            if (_active?.AudioFileId == audioFileId)
                _active.SetAudioGainDb(gainDb);
        }
    }

    private async Task RunScenarioAsync(
        Scenario scenario,
        string deviceId,
        Dictionary<Guid, string> mappings,
        CancellationToken cancellationToken)
    {
        try
        {
            var steps = scenario.Steps.OrderBy(x => x.Order).ToList();

            do
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var step = steps[i];
                    var stepNumber = i + 1;

                    var runtime = new PlaybackRuntime(
                        step.RoomId,
                        step.AudioFileId,
                        step.Room.Volume,
                        step.AudioFile.VolumeDb);

                    lock (_sync) _active = runtime;

                    // Delay belongs to the step itself: it is the time to wait
                    // BEFORE this step starts. This also makes the first step
                    // support an intentional startup delay.
                    if (step.DelayMs > 0)
                    {
                        var remaining = step.DelayMs / 1000d;
                        SetStepState(stepNumber, "waiting", 0, remaining);

                        var delayStart = DateTime.UtcNow;
                        while (remaining > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            lock (_sync)
                            {
                                if (runtime.SkipRequested) break;
                            }

                            await Task.Delay(100, cancellationToken);
                            remaining = Math.Max(0, step.DelayMs / 1000d - (DateTime.UtcNow - delayStart).TotalSeconds);
                            SetStepState(stepNumber, "waiting", 0, remaining);
                        }

                        if (runtime.SkipRequested)
                        {
                            lock (_sync)
                            {
                                if (_active == runtime) _active = null;
                            }
                            SetStepState(stepNumber, "stopped", 0, 0);
                            continue;
                        }
                    }

                    SetStepState(stepNumber, "playing", 0, 0);

                    await PlayFileAsync(
                        deviceId,
                        mappings[step.RoomId],
                        step.AudioFile,
                        scenario,
                        step,
                        stepNumber,
                        steps.Count,
                        runtime,
                        cancellationToken);

                    if (runtime.SkipRequested)
                    {
                        lock (_sync)
                        {
                            if (_active == runtime) _active = null;
                        }
                        SetStepState(stepNumber, "stopped", 0, 0);
                        continue;
                    }

                    SetStepState(stepNumber, "finished", step.AudioFile.Duration.TotalSeconds, 0);

                    lock (_sync)
                    {
                        if (_active == runtime) _active = null;
                    }
                }
            } while (string.Equals(scenario.Repeat, "loop", StringComparison.OrdinalIgnoreCase));

            SetStateTerminal("idle", scenario);
        }
        catch (OperationCanceledException)
        {
            SetStateTerminal("stopped", scenario);
        }
        catch (Exception ex)
        {
            SetStateTerminal("error", scenario, ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                _active?.Stop();
                _active = null;
                _runCts?.Dispose();
                _runCts = null;
                _runTask = null;
            }
        }
    }

    private async Task PlayFileAsync(
        string deviceId,
        string outputId,
        AudioFile audio,
        Scenario scenario,
        ScenarioStep step,
        int stepNumber,
        int totalSteps,
        PlaybackRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlaybackException("UNSUPPORTED_PLATFORM", "Audio playback is supported on Windows only.");

        var path = GetAudioPath(audio.StorageFileName);
        using var source = OpenSource(path);
        using var output = CreateOutput(deviceId, outputId, out var targetFormat, out var targetChannel);

        using var provider = new RoutedWaveProvider(
            source,
            targetFormat,
            targetChannel,
            runtime);

        runtime.Attach(output, provider);

        output.Init(provider);
        output.Play();

        var started = DateTime.UtcNow;
        var duration = Math.Max(0.001, audio.Duration.TotalSeconds);

        try
        {
            while (output.PlaybackState != PlaybackState.Stopped)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (runtime.SkipRequested)
                {
                    output.Stop();
                    break;
                }

                if (runtime.PauseRequested && output.PlaybackState == PlaybackState.Playing)
                {
                    output.Pause();
                    SetStepState(stepNumber, "paused", runtime.PausedPosition, 0);
                    while (runtime.PauseRequested && !runtime.SkipRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(100, cancellationToken);
                    }

                    if (runtime.SkipRequested)
                    {
                        output.Stop();
                        break;
                    }

                    output.Play();
                    started = DateTime.UtcNow - TimeSpan.FromSeconds(runtime.PausedPosition);
                }

                var position = Math.Min(duration, (DateTime.UtcNow - started).TotalSeconds);
                if (output.PlaybackState == PlaybackState.Playing)
                {
                    runtime.PausedPosition = position;
                    SetStepState(stepNumber, "playing", position, 0);
                }

                // Some output backends can remain in Playing state for a short
                // time after the provider has reached EOF (especially when a
                // resampler is involved). Never leave the UI stuck at 100%.
                if (runtime.EndOfStream)
                {
                    output.Stop();
                    break;
                }

                await Task.Delay(100, cancellationToken);

                if (output.PlaybackState == PlaybackState.Stopped)
                    break;
            }
        }
        finally
        {
            output.Stop();
            runtime.Detach();
        }
    }

    private void SetStepState(int stepNumber, string status, double position, double delayRemaining)
    {
        lock (_sync)
        {
            if (_state.Steps.Count == 0) return;
            var steps = _state.Steps.ToList();
            var index = Math.Clamp(stepNumber - 1, 0, steps.Count - 1);
            var current = steps[index];
            steps[index] = current with
            {
                Status = status,
                PositionSeconds = position,
                DelayRemainingSeconds = delayRemaining
            };

            var next = status is "playing" or "waiting"
                ? (stepNumber < steps.Count ? stepNumber + 1 : null)
                : FindNextPending(steps, stepNumber);

            _state = _state with
            {
                Status = status switch
                {
                    "waiting" => "waiting",
                    "playing" => "playing",
                    "paused" => "paused",
                    _ => _state.Status
                },
                CurrentStep = stepNumber,
                NextStep = next,
                Rooms = BuildRoomStates(steps),
                Steps = steps
            };
        }
    }

    private static int? FindNextPending(List<PlaybackStepStateDto> steps, int current)
    {
        return steps
            .Select((step, index) => new { step, index })
            .Where(x => x.index + 1 > current && x.step.Status == "pending")
            .Select(x => (int?)(x.index + 1))
            .FirstOrDefault();
    }

    private IReadOnlyList<PlaybackRoomStateDto> BuildRoomStates(List<PlaybackStepStateDto> steps)
    {
        lock (_sync)
        {
            var active = _active;
            if (active is null) return [];

            var current = steps.FirstOrDefault(x => x.Step == _state.CurrentStep);
            if (current is null) return [];

            return [new PlaybackRoomStateDto(
                current.RoomId,
                ToRoomStatus(current.Status),
                _state.ScenarioId,
                _state.ScenarioName,
                current.AudioFileId,
                current.AudioFileName,
                current.OutputId,
                current.Step,
                steps.Count,
                current.PositionSeconds,
                current.DurationSeconds,
                active.RoomVolume,
                active.AudioGainDb,
                current.Error)];
        }
    }

    private static string ToRoomStatus(string stepStatus)
    {
        return stepStatus switch
        {
            "playing" => "playing",
            "waiting" or "paused" => "waiting",
            "error" => "error",
            _ => "stopped"
        };
    }

    private void SetState(string status, Scenario scenario, int step)
    {
        lock (_sync)
        {
            _state = _state with
            {
                Status = status,
                ScenarioId = scenario.Id,
                ScenarioName = scenario.Name,
                CurrentStep = step,
                NextStep = step < _state.TotalSteps ? step + 1 : null,
                Rooms = BuildRoomStates(_state.Steps.ToList())
            };
        }
    }

    private void SetStateTerminal(string status, Scenario scenario, string? error = null)
    {
        lock (_sync)
        {
            var steps = _state.Steps.ToList();
            if (status == "idle")
            {
                for (var i = 0; i < steps.Count; i++)
                    if (steps[i].Status is "pending" or "playing" or "waiting")
                        steps[i] = steps[i] with { Status = "finished" };
            }

            _state = _state with
            {
                Status = status,
                ScenarioId = scenario.Id,
                ScenarioName = scenario.Name,
                FinishedAt = DateTime.UtcNow,
                CurrentStep = Math.Min(_state.CurrentStep, steps.Count),
                NextStep = null,
                Rooms = [],
                Steps = steps,
                Error = error
            };
        }
    }

    private static string GetAudioPath(string storageFileName)
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataPath, "Audexa", "Audio", storageFileName);
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
        if (outputMarker >= 0 && int.TryParse(outputId[(outputMarker + 8)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return index;

        throw new PlaybackException("INVALID_OUTPUT", $"Cannot determine output channel from '{outputId}'.");
    }

    private sealed class PlaybackRuntime
    {
        private readonly object _sync = new();
        private RoutedWaveProvider? _provider;
        private IWavePlayer? _output;

        public PlaybackRuntime(Guid roomId, Guid audioFileId, int roomVolume, int audioGainDb)
        {
            RoomId = roomId;
            AudioFileId = audioFileId;
            RoomVolume = Math.Clamp(roomVolume, 0, 100);
            AudioGainDb = Math.Clamp(audioGainDb, -24, 24);
        }

        public Guid RoomId { get; }
        public Guid AudioFileId { get; }
        public int RoomVolume { get; private set; }
        public int AudioGainDb { get; private set; }
        public bool SkipRequested { get; private set; }
        public bool PauseRequested { get; private set; }
        public bool EndOfStream { get; private set; }
        public double PausedPosition { get; set; }

        public void Attach(IWavePlayer output, RoutedWaveProvider provider)
        {
            lock (_sync)
            {
                _output = output;
                _provider = provider;
            }
        }

        public void Detach()
        {
            lock (_sync)
            {
                _output = null;
                _provider = null;
            }
        }

        public void SetRoomVolume(int volume)
        {
            lock (_sync) RoomVolume = Math.Clamp(volume, 0, 100);
        }

        public void SetAudioGainDb(int gainDb)
        {
            lock (_sync) AudioGainDb = Math.Clamp(gainDb, -24, 24);
        }

        public void RequestSkip()
        {
            lock (_sync)
            {
                SkipRequested = true;
                _output?.Stop();
            }
        }

        public void Pause()
        {
            lock (_sync) PauseRequested = true;
        }

        public void Resume()
        {
            lock (_sync) PauseRequested = false;
        }

        public void MarkEndOfStream()
        {
            lock (_sync) EndOfStream = true;
        }

        public void Stop()
        {
            lock (_sync)
            {
                SkipRequested = true;
                _output?.Stop();
            }
        }

        public double GetGain()
        {
            lock (_sync)
            {
                return Math.Clamp(RoomVolume / 100d, 0d, 1d) * Math.Pow(10d, AudioGainDb / 20d);
            }
        }
    }

    private sealed class RoutedWaveProvider : IWaveProvider, IDisposable
    {
        private readonly WaveStream _source;
        private readonly WaveFormat _target;
        private readonly int _channel;
        private readonly PlaybackRuntime _runtime;
        private readonly MediaFoundationResampler? _resampler;
        private readonly IWaveProvider _input;

        public RoutedWaveProvider(WaveStream source, WaveFormat target, int channel, PlaybackRuntime runtime)
        {
            _source = source;
            _target = target;
            _channel = Math.Clamp(channel, 0, Math.Max(0, target.Channels - 1));
            _runtime = runtime;

            if (source.WaveFormat.SampleRate != target.SampleRate || source.WaveFormat.Channels != 1 || source.WaveFormat.BitsPerSample != 16)
            {
                _resampler = new MediaFoundationResampler(source, new WaveFormat(target.SampleRate, 16, 1));
                _resampler.ResamplerQuality = 60;
                _input = _resampler;
            }
            else
            {
                _input = source;
            }
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
            if (sourceFrames == 0)
            {
                _runtime.MarkEndOfStream();
                return 0;
            }

            var bytesWritten = sourceFrames * bytesPerFrame;
            Array.Clear(buffer, offset, bytesWritten);
            var gain = _runtime.GetGain();

            for (var frame = 0; frame < sourceFrames; frame++)
            {
                var sample = BitConverter.ToInt16(sourceBuffer, frame * 2) / 32768f * (float)gain;
                var sampleOffset = offset + frame * bytesPerFrame + _channel * (_target.BitsPerSample / 8);

                if (_target.Encoding == WaveFormatEncoding.IeeeFloat && _target.BitsPerSample == 32)
                    BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), sample);
                else if (_target.Encoding == WaveFormatEncoding.Pcm && _target.BitsPerSample == 16)
                    BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 2), (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue));
                else if (_target.Encoding == WaveFormatEncoding.Pcm && _target.BitsPerSample == 32)
                    BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), (int)Math.Clamp(sample * int.MaxValue, int.MinValue, int.MaxValue));
                else
                    throw new PlaybackException("UNSUPPORTED_FORMAT", $"Unsupported target format {_target}");
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
