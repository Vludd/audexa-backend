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

    [HttpPost("pause")]
    public async Task<ActionResult<PlaybackStateDto>> Pause(CancellationToken cancellationToken)
    {
        await _playback.PauseAsync(cancellationToken);
        return Ok(_playback.GetState());
    }

    [HttpPost("resume")]
    public async Task<ActionResult<PlaybackStateDto>> Resume(CancellationToken cancellationToken)
    {
        await _playback.ResumeAsync(cancellationToken);
        return Ok(_playback.GetState());
    }

    [HttpPost("stop")]
    public async Task<ActionResult<PlaybackStateDto>> Stop(CancellationToken cancellationToken)
    {
        await _playback.StopAsync(cancellationToken);
        return Ok(_playback.GetState());
    }

    [HttpPost("steps/{roomId:guid}/skip")]
    public async Task<ActionResult<PlaybackStateDto>> SkipStep(Guid roomId, CancellationToken cancellationToken)
    {
        try
        {
            await _playback.SkipStepAsync(roomId, cancellationToken);
            return Ok(_playback.GetState());
        }
        catch (PlaybackException ex)
        {
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
    }

    [HttpPatch("rooms/{id:guid}/volume")]
    public async Task<ActionResult<PlaybackStateDto>> SetRoomVolume(Guid id, [FromBody] PlaybackVolumeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _playback.SetRoomVolumeAsync(id, request.Value, cancellationToken);
            return Ok(_playback.GetState());
        }
        catch (PlaybackException ex)
        {
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
    }

    [HttpPatch("audio/{id:guid}/gain")]
    public async Task<ActionResult<PlaybackStateDto>> SetAudioGain(Guid id, [FromBody] PlaybackVolumeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _playback.SetAudioGainAsync(id, request.Value, cancellationToken);
            return Ok(_playback.GetState());
        }
        catch (PlaybackException ex)
        {
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
    }
}
