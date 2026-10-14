namespace audexa_backend.DTOs.Playback;

public sealed record PlaybackStartRequest(Guid ScenarioId);

public sealed record PlaybackRoomStateDto(
    Guid RoomId,
    string Status,
    Guid? ScenarioId,
    string? ScenarioName,
    Guid? AudioFileId,
    string? AudioFileName,
    string? OutputId,
    int Step,
    int TotalSteps,
    double PositionSeconds,
    double DurationSeconds,
    string? Error);

public sealed record PlaybackStateDto(
    string Status,
    Guid? ScenarioId,
    string? ScenarioName,
    int CurrentStep,
    int TotalSteps,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    IReadOnlyList<PlaybackRoomStateDto> Rooms,
    string? Error);
