using audexa_backend.Data;
using Microsoft.EntityFrameworkCore;

namespace audexa_backend.Services;

public sealed class ScenarioSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlaybackEngine _playback;
    private readonly ILogger<ScenarioSchedulerService> _logger;
    private readonly Dictionary<int, DateTime> _lastTriggered = [];

    public ScenarioSchedulerService(
        IServiceScopeFactory scopeFactory,
        IPlaybackEngine playback,
        ILogger<ScenarioSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _playback = playback;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogError(new EventId(7101, "schedule.tick_failed"), ex, "Scheduler tick failed. ErrorType={ErrorType}", ex.GetType().Name); }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var weekday = now.DayOfWeek.ToString()[..3].ToLowerInvariant();
        var time = new TimeSpan(now.Hour, now.Minute, 0);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var schedules = await db.Schedules.AsNoTracking()
            .Include(x => x.Scenario)
            .Where(x => x.Enabled && x.Time == time && x.Scenario.Status == "active")
            .ToListAsync(cancellationToken);

        foreach (var schedule in schedules)
        {
            var days = schedule.Days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!days.Contains(weekday, StringComparer.OrdinalIgnoreCase)) continue;

            var occurrence = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            if (_lastTriggered.TryGetValue(schedule.Id, out var previous) && previous == occurrence) continue;

            if (string.Equals(schedule.Repeat, "once", StringComparison.OrdinalIgnoreCase))
            {
                // The current Schedule model has no execution date, so "once" is
                // only safe to treat as once per backend process. A real calendar
                // date should be added before relying on persistent one-shot events.
                if (_lastTriggered.ContainsKey(schedule.Id)) continue;
            }

            _lastTriggered[schedule.Id] = occurrence;
            try
            {
                await _playback.StartScenarioAsync(schedule.ScenarioId, cancellationToken);
                _logger.LogInformation(new EventId(7102, "schedule.execution_started"), "Schedule execution started. ScheduleId={ScheduleId}; ScenarioId={ScenarioId}", schedule.Id, schedule.ScenarioId);
            }
            catch (PlaybackException ex)
            {
                _logger.LogError(new EventId(7103, "schedule.execution_failed"), ex, "Schedule execution failed. ScheduleId={ScheduleId}; ScenarioId={ScenarioId}; ErrorCode={ErrorCode}; Message={Message}", schedule.Id, schedule.ScenarioId, ex.Code, ex.Message);
            }
        }
    }
}
