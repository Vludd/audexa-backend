using audexa_backend.DTOs.Playback;

namespace audexa_backend.Services;

public interface IPlaybackEngine
{
    PlaybackStateDto GetState();
    Task<PlaybackStateDto> StartScenarioAsync(Guid scenarioId, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SkipStepAsync(Guid roomId, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task SetRoomVolumeAsync(Guid roomId, int volume, CancellationToken cancellationToken = default);
    Task SetAudioGainAsync(Guid audioFileId, int gainDb, CancellationToken cancellationToken = default);
}
