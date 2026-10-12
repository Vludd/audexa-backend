using audexa_backend.Data;
using audexa_backend.DTOs;
using audexa_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace audexa_backend.Services;

public sealed class RoomService
{
    private readonly AppDbContext _db;

    public RoomService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyCollection<RoomDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await _db.Rooms
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new RoomDto(
                x.Id,
                x.Name,
                x.AudioFileId,
                x.Volume))
            .ToListAsync(cancellationToken);
    }

    public async Task<RoomDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _db.Rooms
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new RoomDto(
                x.Id,
                x.Name,
                x.AudioFileId,
                x.Volume))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<RoomDto> CreateAsync(
        CreateRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();

        ValidateName(name);
        ValidateVolume(request.Volume);

        if (request.AudioFileId.HasValue)
        {
            await ValidateAudioFileAsync(
                request.AudioFileId.Value,
                cancellationToken);
        }

        var room = new Room
        {
            Id = Guid.NewGuid(),
            Name = name,
            AudioFileId = request.AudioFileId,
            Volume = request.Volume
        };

        _db.Rooms.Add(room);

        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(room);
    }

    public async Task<RoomDto?> UpdateAsync(
        Guid id,
        UpdateRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        var room = await _db.Rooms
            .FirstOrDefaultAsync(
                x => x.Id == id,
                cancellationToken);

        if (room is null)
        {
            return null;
        }

        var name = request.Name.Trim();

        ValidateName(name);
        ValidateVolume(request.Volume);

        if (request.AudioFileId.HasValue)
        {
            await ValidateAudioFileAsync(
                request.AudioFileId.Value,
                cancellationToken);
        }

        room.Name = name;
        room.AudioFileId = request.AudioFileId;
        room.Volume = request.Volume;

        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(room);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var room = await _db.Rooms
            .FirstOrDefaultAsync(
                x => x.Id == id,
                cancellationToken);

        if (room is null)
        {
            return false;
        }

        _db.Rooms.Remove(room);

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    private async Task ValidateAudioFileAsync(
        Guid audioFileId,
        CancellationToken cancellationToken)
    {
        var exists = await _db.AudioFiles
            .AnyAsync(
                x => x.Id == audioFileId,
                cancellationToken);

        if (!exists)
        {
            throw new RoomValidationException(
                "AUDIO_FILE_NOT_FOUND",
                $"Audio file '{audioFileId}' was not found.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new RoomValidationException(
                "INVALID_NAME",
                "Room name is required.");
        }

        if (name.Length > 200)
        {
            throw new RoomValidationException(
                "INVALID_NAME",
                "Room name cannot exceed 200 characters.");
        }
    }

    private static void ValidateVolume(int volume)
    {
        if (volume is < 0 or > 100)
        {
            throw new RoomValidationException(
                "INVALID_VOLUME",
                "Room volume must be between 0 and 100.");
        }
    }

    private static RoomDto ToDto(Room room)
    {
        return new RoomDto(
            room.Id,
            room.Name,
            room.AudioFileId,
            room.Volume);
    }
}

public sealed class RoomValidationException : Exception
{
    public RoomValidationException(
        string code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}