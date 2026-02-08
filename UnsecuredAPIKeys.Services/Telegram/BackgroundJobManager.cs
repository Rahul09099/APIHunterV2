using System.Collections.Concurrent;

namespace UnsecuredAPIKeys.Services.Telegram;

public class BackgroundJobManager
{
    private readonly ConcurrentDictionary<string, JobInfo> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();

    public string StartJob(string jobType, Func<CancellationToken, Task> jobTask)
    {
        var jobId = Guid.NewGuid().ToString();
        var cts = new CancellationTokenSource();
        
        var jobInfo = new JobInfo
        {
            JobId = jobId,
            JobType = jobType,
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
                _cancellationTokens.TryRemove(jobId, out _);
            }
        }, cts.Token);

        return jobId;
    }

    public bool StopJob(string jobId)
    {
        if (_cancellationTokens.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
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
}

public class JobInfo
{
    public string JobId { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}
