using System.Globalization;
using audexa_backend.Data;
using audexa_backend.DTOs.Schedules;
using audexa_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace audexa_backend.Services;

public sealed class ScheduleService
{
    private static readonly HashSet<string> ValidDays = new(StringComparer.OrdinalIgnoreCase)
    {
        "mon", "tue", "wed", "thu", "fri", "sat", "sun"
    };

    private static readonly HashSet<string> ValidRepeats = new(StringComparer.OrdinalIgnoreCase)
    {
        "daily", "weekly", "once"
    };

    private readonly AppDbContext _db;

    public ScheduleService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ScheduleDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var schedules = await _db.Schedules
            .AsNoTracking()
            .Include(x => x.Scenario)
            .ToListAsync(cancellationToken);

        return schedules
            .OrderBy(x => x.Time)
            .ThenBy(x => x.Id)
            .Select(ToDto)
            .ToList();
    }

    public async Task<ScheduleDto?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var schedule = await _db.Schedules
            .AsNoTracking()
            .Include(x => x.Scenario)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        return schedule is null ? null : ToDto(schedule);
    }

    public async Task<ScheduleDto> CreateAsync(
        CreateScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request.Time, request.ScenarioId, request.Days, request.Repeat, request.Enabled);
        await ValidateAsync(normalized, cancellationToken);

        var now = DateTime.UtcNow;
        var schedule = new Schedule
        {
            Time = normalized.Time,
            ScenarioId = normalized.ScenarioId,
            Days = string.Join(',', normalized.Days),
            Repeat = normalized.Repeat,
            Enabled = normalized.Enabled,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Schedules.Add(schedule);
        await _db.SaveChangesAsync(cancellationToken);

        schedule.Scenario = await _db.Scenarios
            .AsNoTracking()
            .FirstAsync(x => x.Id == schedule.ScenarioId, cancellationToken);

        return ToDto(schedule);
    }

    public async Task<ScheduleDto?> UpdateAsync(
        int id,
        UpdateScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        var schedule = await _db.Schedules
            .Include(x => x.Scenario)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (schedule is null)
        {
            return null;
        }

        var normalized = Normalize(request.Time, request.ScenarioId, request.Days, request.Repeat, request.Enabled);
        await ValidateAsync(normalized, cancellationToken);

        schedule.Time = normalized.Time;
        schedule.ScenarioId = normalized.ScenarioId;
        schedule.Days = string.Join(',', normalized.Days);
        schedule.Repeat = normalized.Repeat;
        schedule.Enabled = normalized.Enabled;
        schedule.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        schedule.Scenario = await _db.Scenarios
            .AsNoTracking()
            .FirstAsync(x => x.Id == schedule.ScenarioId, cancellationToken);

        return ToDto(schedule);
    }

    public async Task<ScheduleDto?> ToggleAsync(
        int id,
        ToggleScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        var schedule = await _db.Schedules
            .Include(x => x.Scenario)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (schedule is null)
        {
            return null;
        }

        schedule.Enabled = request.Enabled;
        schedule.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(schedule);
    }

    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var schedule = await _db.Schedules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (schedule is null)
        {
            return false;
        }

        _db.Schedules.Remove(schedule);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ValidateAsync(
        NormalizedSchedule schedule,
        CancellationToken cancellationToken)
    {
        if (!await _db.Scenarios.AnyAsync(x => x.Id == schedule.ScenarioId, cancellationToken))
        {
            throw new ScheduleValidationException(
                "SCENARIO_NOT_FOUND",
                "Selected scenario was not found.");
        }
    }

    private static NormalizedSchedule Normalize(
        string time,
        Guid scenarioId,
        IReadOnlyList<string>? days,
        string repeat,
        bool enabled)
    {
        if (!TimeSpan.TryParseExact(
                time,
                "hh\\:mm",
                CultureInfo.InvariantCulture,
                TimeSpanStyles.None,
                out var parsedTime) &&
            !TimeSpan.TryParseExact(
                time,
                "h\\:mm",
                CultureInfo.InvariantCulture,
                TimeSpanStyles.None,
                out parsedTime))
        {
            throw new ScheduleValidationException("INVALID_TIME", "Time must use HH:mm format.");
        }

        if (parsedTime < TimeSpan.Zero || parsedTime >= TimeSpan.FromDays(1))
        {
            throw new ScheduleValidationException("INVALID_TIME", "Time must be between 00:00 and 23:59.");
        }

        if (scenarioId == Guid.Empty)
        {
            throw new ScheduleValidationException("INVALID_SCENARIO", "A scenario is required.");
        }

        if (!ValidRepeats.Contains(repeat))
        {
            throw new ScheduleValidationException("INVALID_REPEAT", "Repeat must be daily, weekly, or once.");
        }

        var normalizedDays = (days ?? [])
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        if (normalizedDays.Count == 0 || normalizedDays.Any(x => !ValidDays.Contains(x)))
        {
            throw new ScheduleValidationException(
                "INVALID_DAYS",
                "At least one valid weekday is required.");
        }

        normalizedDays.Sort((left, right) =>
            Array.IndexOf(["mon", "tue", "wed", "thu", "fri", "sat", "sun"], left)
                .CompareTo(Array.IndexOf(["mon", "tue", "wed", "thu", "fri", "sat", "sun"], right)));

        return new NormalizedSchedule(parsedTime, scenarioId, normalizedDays, repeat.ToLowerInvariant(), enabled);
    }

    private static ScheduleDto ToDto(Schedule schedule)
    {
        return new ScheduleDto(
            schedule.Id,
            schedule.Time.ToString(@"hh\:mm"),
            schedule.ScenarioId,
            schedule.Scenario.Name,
            schedule.Days
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            schedule.Repeat,
            schedule.Enabled,
            schedule.Enabled ? "active" : "inactive",
            schedule.CreatedAt,
            schedule.UpdatedAt);
    }

    private sealed record NormalizedSchedule(
        TimeSpan Time,
        Guid ScenarioId,
        List<string> Days,
        string Repeat,
        bool Enabled);
}

public sealed class ScheduleValidationException : Exception
{
    public ScheduleValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
