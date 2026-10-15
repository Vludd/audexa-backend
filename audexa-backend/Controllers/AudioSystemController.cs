using audexa_backend.Data;
using audexa_backend.DTOs.System;
using audexa_backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace audexa_backend.Controllers;

[ApiController]
[Route("api/audio-system")]
public sealed class AudioSystemController : ControllerBase
{
    private static readonly Guid ConfigurationId =
        Guid.Parse("6d7c1f5b-6c83-4e0d-8a4a-5b7c5f4b7f01");

    private readonly AppDbContext _db;
    private readonly IAudioDeviceService _devices;
    private readonly IPlaybackEngine _engine;

    public AudioSystemController(AppDbContext db, IAudioDeviceService devices, IPlaybackEngine engine)
    {
        _db = db;
        _devices = devices;
        _engine = engine;
    }

    [HttpGet("status")]
    public async Task<ActionResult<SystemStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var configuration = await _db.AudioConfigurations
            .AsNoTracking()
            .Include(x => x.Mappings)
            .SingleOrDefaultAsync(x => x.Id == ConfigurationId, cancellationToken);
        var roomIds = await _db.Rooms.AsNoTracking().Select(x => x.Id).ToListAsync(cancellationToken);
        var playback = _engine.GetState();
        var engineStatus = playback.Status switch
        {
            "starting" => "starting",
            "playing" or "paused" or "waiting" => "running",
            "error" => "error",
            _ => "stopped"
        };

        if (string.IsNullOrWhiteSpace(configuration?.DeviceId))
        {
            return Ok(new SystemStatusDto("unconfigured", "online", null, null, false, 0, 0, 0,
                roomIds.Count, null, null, engineStatus,
                "Аудиоустройство не выбрано",
                DateTime.UtcNow));
        }

        var device = _devices.FindDevice(configuration.DeviceId);
        if (device is null || !string.Equals(device.Status, "online", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new SystemStatusDto("disconnected", "online", device?.Name ?? "Неизвестное аудиоустройство",
                configuration.DeviceId, false, device?.OutputCount ?? 0, 0, 0, roomIds.Count,
                device?.SampleRate, device?.BufferSize, engineStatus,
                "Настроенное аудиоустройство недоступно", DateTime.UtcNow));
        }

        var outputIds = new HashSet<string>(device.Outputs.Select(x => x.Id), StringComparer.Ordinal);
        var validMappings = configuration.Mappings
            .Where(x => roomIds.Contains(x.RoomId) && outputIds.Contains(x.OutputId))
            .ToList();
        var duplicateOutputs = validMappings.GroupBy(x => x.OutputId, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
        var mappedRoomIds = validMappings.Select(x => x.RoomId).Distinct().ToHashSet();
        // An empty room list is not an audio-device failure: there are no rooms
        // that require output mappings yet. Keep hardware readiness separate from
        // whether the operator has created room/scenario content.
        var allRoomsMapped = roomIds.Count == 0 || roomIds.All(mappedRoomIds.Contains);
        var mappingsValid = allRoomsMapped && !duplicateOutputs;
        var healthy = mappingsValid && engineStatus != "error";
        var state = engineStatus == "error" ? "error" : healthy ? "ready" : "degraded";
        string? error = null;
        if (duplicateOutputs) error = "Один или несколько аудиовыходов назначены нескольким комнатам";
        else if (roomIds.Count > 0 && !mappingsValid) error = $"Корректно сопоставлено комнат: {mappedRoomIds.Count} из {roomIds.Count}";
        else if (engineStatus == "error") error = playback.Error ?? "Ошибка движка воспроизведения";

        return Ok(new SystemStatusDto(state, "online", device.Name, device.Id, true, device.OutputCount,
            device.Outputs.Count, mappedRoomIds.Count, roomIds.Count, device.SampleRate,
            device.BufferSize, engineStatus, error, DateTime.UtcNow));
    }
}
