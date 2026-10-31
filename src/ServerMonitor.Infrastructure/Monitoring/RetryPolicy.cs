using ServerMonitor.Infrastructure.Remote;

namespace ServerMonitor.Infrastructure.Monitoring;

public static class RetryPolicy
{
    /// <summary>
    /// Retries transient failures with exponential backoff + jitter (base, 2×base, 4×base ...).
    /// Authentication and host-key errors are never retried (retrying would only lock accounts or hide MITM).
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, int maxAttempts, TimeSpan baseDelay, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(ct);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) * (0.8 + Random.Shared.NextDouble() * 0.4));
                await Task.Delay(delay, ct);
            }
        }
    }

    public static bool IsTransient(Exception ex) => ex switch
    {
        RemoteConnectionException rce => rce.IsTransient,
        RemoteCommandException => true,
        TimeoutException => true,
        System.Net.Sockets.SocketException => true,
        IOException => true,
        _ => false
    };

    /// <summary>Delay before the next poll of an unreachable host: doubles per failure, at most 4x the interval and 5 minutes.</summary>
    public static TimeSpan BackoffInterval(TimeSpan normal, int consecutiveFailures)
    {
        if (consecutiveFailures <= 1) return normal;
        var factor = Math.Pow(2, Math.Min(consecutiveFailures - 1, 2));
        var next = TimeSpan.FromSeconds(normal.TotalSeconds * factor);
        return next > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : next;
    }
}
