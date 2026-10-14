using audexa_backend.DTOs.Playback;

namespace audexa_backend.Services;

public interface IPlaybackEngine
{
    PlaybackStateDto GetState();
    Task<PlaybackStateDto> StartScenarioAsync(Guid scenarioId, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task StopRoomAsync(Guid roomId, CancellationToken cancellationToken = default);
}
