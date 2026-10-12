namespace audexa_backend.DTOs;

public sealed record RoomDto(
    Guid Id,
    string Name,
    Guid? AudioFileId,
    int Volume
);

public sealed record CreateRoomRequest(
    string Name,
    Guid? AudioFileId,
    int Volume = 100
);

public sealed record UpdateRoomRequest(
    string Name,
    Guid? AudioFileId,
    int Volume = 100
);