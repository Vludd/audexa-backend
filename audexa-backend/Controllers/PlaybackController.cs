using audexa_backend.DTOs.Playback;
using audexa_backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace audexa_backend.Controllers;

[ApiController]
[Route("api/playback")]
public sealed class PlaybackController : ControllerBase
{
    private readonly IPlaybackEngine _playback;

    public PlaybackController(IPlaybackEngine playback) => _playback = playback;

    [HttpGet]
    public ActionResult<PlaybackStateDto> GetState() => Ok(_playback.GetState());

    [HttpPost("scenarios/{id:guid}/start")]
    public async Task<ActionResult<PlaybackStateDto>> StartScenario(Guid id, CancellationToken cancellationToken)
    {
        try { return Ok(await _playback.StartScenarioAsync(id, cancellationToken)); }
        catch (PlaybackException ex) { return BadRequest(new { code = ex.Code, message = ex.Message }); }
    }

    [HttpPost("stop")]
    public async Task<ActionResult<PlaybackStateDto>> Stop(CancellationToken cancellationToken)
    {
        await _playback.StopAsync(cancellationToken);
        return Ok(_playback.GetState());
    }

    [HttpPost("rooms/{id:guid}/stop")]
    public async Task<ActionResult<PlaybackStateDto>> StopRoom(Guid id, CancellationToken cancellationToken)
    {
        await _playback.StopRoomAsync(id, cancellationToken);
        return Ok(_playback.GetState());
    }
}
