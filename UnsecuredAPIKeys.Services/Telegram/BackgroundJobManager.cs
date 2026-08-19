using System.Collections.Concurrent;

namespace UnsecuredAPIKeys.Services.Telegram;

public class BackgroundJobManager
{
    private readonly ConcurrentDictionary<string, JobInfo> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();

    public string StartJob(string jobType, Func<CancellationToken, Task> jobTask, long? ownerTelegramId = null)
    {
        PruneJobHistory();

        var jobId = Guid.NewGuid().ToString();
        var cts = new CancellationTokenSource();
        
        var jobInfo = new JobInfo
        {
            JobId = jobId,
            JobType = jobType,
            OwnerTelegramId = ownerTelegramId,
            Status = "Running",
            StartedAt = DateTime.UtcNow
        };

        _jobs[jobId] = jobInfo;
        _cancellationTokens[jobId] = cts;

        // Run the job in the background
        _ = Task.Run(async () =>
        {
            try
            {
                await jobTask(cts.Token);
                jobInfo.Status = "Completed";
                jobInfo.CompletedAt = DateTime.UtcNow;
            }
            catch (OperationCanceledException)
            {
                jobInfo.Status = "Cancelled";
                jobInfo.CompletedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                jobInfo.Status = "Failed";
                jobInfo.Error = ex.Message;
                jobInfo.CompletedAt = DateTime.UtcNow;
            }
            finally
            {
                if (_cancellationTokens.TryRemove(jobId, out var removedCts))
                {
                    try { removedCts.Dispose(); } catch { }
                }
            }
        }, cts.Token);

        return jobId;
    }

    public bool StopJob(string jobId)
    {
        if (_cancellationTokens.TryGetValue(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            return true;
        }
        return false;
    }

    public JobInfo? GetJobInfo(string jobId)
    {
        _jobs.TryGetValue(jobId, out var jobInfo);
        return jobInfo;
    }

    public IEnumerable<JobInfo> GetAllJobs()
    {
        return _jobs.Values.OrderByDescending(j => j.StartedAt);
    }

    public void ClearCompletedJobs()
    {
        var completedJobs = _jobs.Where(kvp => 
            kvp.Value.Status == "Completed" || 
            kvp.Value.Status == "Failed" || 
            kvp.Value.Status == "Cancelled")
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var jobId in completedJobs)
        {
            _jobs.TryRemove(jobId, out _);
        }
    }

    private void PruneJobHistory()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            var finishedJobs = _jobs.Where(kvp =>
                (kvp.Value.Status == "Completed" || kvp.Value.Status == "Failed" || kvp.Value.Status == "Cancelled") &&
                (kvp.Value.CompletedAt == null || kvp.Value.CompletedAt < cutoff))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in finishedJobs)
            {
                _jobs.TryRemove(id, out _);
            }

            // Keep at most 50 total jobs
            if (_jobs.Count > 50)
            {
                var overflow = _jobs.Values
                    .Where(j => j.Status != "Running")
                    .OrderBy(j => j.StartedAt)
                    .Take(_jobs.Count - 50)
                    .Select(j => j.JobId)
                    .ToList();

                foreach (var id in overflow)
                {
                    _jobs.TryRemove(id, out _);
                }
            }
        }
        catch { }
    }
}

public class JobInfo
{
    public string JobId { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public long? OwnerTelegramId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}
