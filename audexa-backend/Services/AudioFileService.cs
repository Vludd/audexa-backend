using audexa_backend.Data;
using audexa_backend.DTOs.AudioFiles;
using audexa_backend.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NAudio.Wave;

namespace audexa_backend.Services;

public class AudioFileService : IAudioFileService
{
    private static readonly HashSet<string> AllowedExtensions =
    [
        ".wav",
        ".mp3"
    ];

    private const long MaxFileSize = 500L * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly string _audioDirectory;

    public AudioFileService(AppDbContext db)
    {
        _db = db;

        var appDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);

        var audexaDataPath = Path.Combine(appDataPath, "Audexa");

        _audioDirectory = Path.Combine(audexaDataPath, "Audio");

        Directory.CreateDirectory(_audioDirectory);
    }

    public IReadOnlyCollection<AudioFile> GetAll()
    {
        return _db.AudioFiles
            .AsNoTracking()
            .OrderBy(x => x.CreatedAt)
            .ToList();
    }

    public AudioFile? GetById(Guid id)
    {
        return _db.AudioFiles
            .AsNoTracking()
            .FirstOrDefault(x => x.Id == id);
    }

    public AudioFile Create(CreateAudioFileRequest request)
    {
        var audioFile = new AudioFile
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            FileName = request.FileName,
            StorageFileName = request.FileName,
            Format = request.Format,
            Duration = request.Duration,
            Size = request.Size,
            SampleRate = request.SampleRate,
            Channels = request.Channels,
            CreatedAt = DateTime.UtcNow
        };

        _db.AudioFiles.Add(audioFile);
        _db.SaveChanges();

        return audioFile;
    }

    public async Task<AudioFile> UploadAsync(
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file.Length == 0)
        {
            throw new InvalidOperationException("Файл пустой.");
        }

        if (file.Length > MaxFileSize)
        {
            throw new InvalidOperationException(
                "Размер файла не должен превышать 500 MB.");
        }

        var originalFileName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();

        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException(
                "Поддерживаются только WAV и MP3.");
        }

        var id = Guid.NewGuid();
        var storageFileName = $"{id}{extension}";
        var storagePath = Path.Combine(_audioDirectory, storageFileName);

        try
        {
            await using (var stream = new FileStream(
                storagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            var metadata = ReadAudioMetadata(storagePath, extension);

            var audioFile = new AudioFile
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(originalFileName),
                FileName = originalFileName,
                StorageFileName = storageFileName,
                Format = extension.TrimStart('.').ToUpperInvariant(),
                Duration = metadata.Duration,
                Size = file.Length,
                SampleRate = metadata.SampleRate,
                Channels = metadata.Channels,
                CreatedAt = DateTime.UtcNow
            };

            _db.AudioFiles.Add(audioFile);
            await _db.SaveChangesAsync(cancellationToken);

            return audioFile;
        }
        catch
        {
            if (File.Exists(storagePath))
            {
                File.Delete(storagePath);
            }

            throw;
        }
    }

    public AudioFile? Update(Guid id, UpdateAudioFileRequest request)
    {
        var audioFile = _db.AudioFiles
            .FirstOrDefault(x => x.Id == id);

        if (audioFile is null)
        {
            return null;
        }

        audioFile.Name = request.Name;

        _db.SaveChanges();

        return audioFile;
    }

    public bool Delete(Guid id)
    {
        var audioFile = _db.AudioFiles
            .FirstOrDefault(x => x.Id == id);

        if (audioFile is null)
        {
            return false;
        }

        var storagePath = Path.Combine(
            _audioDirectory,
            audioFile.StorageFileName);

        _db.AudioFiles.Remove(audioFile);
        _db.SaveChanges();

        if (File.Exists(storagePath))
        {
            File.Delete(storagePath);
        }

        return true;
    }

    private static AudioMetadata ReadAudioMetadata(
        string path,
        string extension)
        {
            using WaveStream reader = extension switch
            {
                ".wav" => new WaveFileReader(path),
                ".mp3" => new Mp3FileReader(path),
                _ => throw new InvalidOperationException(
                    "Неподдерживаемый формат.")
            };

            return new AudioMetadata(
                reader.TotalTime,
                reader.WaveFormat.SampleRate,
                reader.WaveFormat.Channels);
        }

    private sealed record AudioMetadata(
        TimeSpan Duration,
        int SampleRate,
        int Channels);
}