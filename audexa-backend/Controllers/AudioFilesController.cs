using audexa_backend.DTOs.AudioFiles;
using audexa_backend.Models;
using audexa_backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace audexa_backend.Controllers;

[ApiController]
[Route("api/audio")]
public class AudioFilesController : ControllerBase
{
    private readonly IAudioFileService _audioFileService;

    public AudioFilesController(IAudioFileService audioFileService)
    {
        _audioFileService = audioFileService;
    }

    [HttpGet]
    public ActionResult<IReadOnlyCollection<AudioFile>> GetAll()
    {
        return Ok(_audioFileService.GetAll());
    }

    [HttpGet("{id:guid}")]
    public ActionResult<AudioFile> GetById(Guid id)
    {
        var audioFile = _audioFileService.GetById(id);

        if (audioFile is null)
        {
            return NotFound();
        }

        return Ok(audioFile);
    }

    [HttpPost]
    public ActionResult<AudioFile> Create(CreateAudioFileRequest request)
    {
        var audioFile = _audioFileService.Create(request);

        return CreatedAtAction(
            nameof(GetById),
            new { id = audioFile.Id },
            audioFile
        );
    }

    [HttpPost("upload")]
    public async Task<ActionResult<AudioFile>> Upload(
    IFormFile file,
    CancellationToken cancellationToken)
    {
        try
        {
            var audioFile = await _audioFileService.UploadAsync(
                file,
                cancellationToken);

            return CreatedAtAction(
                nameof(GetById),
                new { id = audioFile.Id },
                audioFile);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    [HttpPut("{id:guid}")]
    public ActionResult<AudioFile> Update(
        Guid id,
        UpdateAudioFileRequest request)
    {
        var audioFile = _audioFileService.Update(id, request);

        if (audioFile is null)
        {
            return NotFound();
        }

        return Ok(audioFile);
    }

    [HttpDelete("{id:guid}")]
    public IActionResult Delete(Guid id)
    {
        var deleted = _audioFileService.Delete(id);

        if (!deleted)
        {
            return NotFound();
        }

        return NoContent();
    }
}