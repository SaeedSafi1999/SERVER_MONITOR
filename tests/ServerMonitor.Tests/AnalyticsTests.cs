using ServerMonitor.Application.Analytics;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;
using ServerMonitor.Infrastructure.Alerting;
using ServerMonitor.Infrastructure.Monitoring;

namespace ServerMonitor.Tests;

public class LatencyStatisticsTests
{
    [Fact]
    public void Percentiles_use_linear_interpolation()
    {
        var samples = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        var s = LatencyStatistics.Compute(samples);
        Assert.Equal(1, s.Min);
        Assert.Equal(100, s.Max);
        Assert.Equal(50.5, s.Average);
        Assert.Equal(50.5, s.P50!.Value, 6);
        Assert.Equal(90.1, s.P90!.Value, 6);
        Assert.Equal(95.05, s.P95!.Value, 6);
        Assert.Equal(99.01, s.P99!.Value, 6);
    }

    [Fact]
    public void Jitter_is_mean_absolute_difference_of_consecutive_samples()
    {
        var s = LatencyStatistics.Compute([10, 14, 12, 20]);
        // |14-10| + |12-14| + |20-12| = 4 + 2 + 8 = 14 / 3
        Assert.Equal(14.0 / 3, s.Jitter!.Value, 6);
        Assert.Equal(20, s.Current);
    }

    [Fact]
    public void Packet_loss_counts_lost_samples()
    {
        var s = LatencyStatistics.Compute([1, 2, 3], lost: 1);
        Assert.Equal(25, s.PacketLossPercent);
    }

    [Fact]
    public void Empty_input_is_all_lost()
    {
        var s = LatencyStatistics.Compute([], lost: 4);
        Assert.Null(s.Average);
        Assert.Equal(100, s.PacketLossPercent);
    }
}

public class AvailabilityCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Computes_uptime_mttr_mtbf()
    {
        var periods = new[]
        {
            new StatusPeriod { IsUp = true, StartedAt = T0, EndedAt = T0.AddHours(10) },
            new StatusPeriod { IsUp = false, StartedAt = T0.AddHours(10), EndedAt = T0.AddHours(10).AddMinutes(30) },
            new StatusPeriod { IsUp = true, StartedAt = T0.AddHours(10).AddMinutes(30), EndedAt = T0.AddHours(20) },
            new StatusPeriod { IsUp = false, StartedAt = T0.AddHours(20), EndedAt = T0.AddHours(20).AddMinutes(90) },
            new StatusPeriod { IsUp = true, StartedAt = T0.AddHours(21).AddMinutes(30), EndedAt = null }
        };
        var r = AvailabilityCalculator.Compute(periods, T0, T0.AddDays(1), 99.9, now: T0.AddDays(1));

        Assert.Equal(2, r.Incidents);
        Assert.Equal(TimeSpan.FromHours(2), r.Downtime);
        Assert.Equal(TimeSpan.FromHours(22), r.Uptime);
        Assert.Equal(TimeSpan.FromHours(1), r.Mttr);            // (30 + 90) / 2 minutes
        Assert.Equal(TimeSpan.FromHours(11), r.Mtbf);           // 22h up / 2 failures
        Assert.Equal(100.0 * 22 / 24, r.UptimePercent, 6);
        Assert.False(r.SlaMet);
    }

    [Fact]
    public void Unmonitored_time_is_excluded_not_counted_as_downtime()
    {
        var periods = new[] { new StatusPeriod { IsUp = true, StartedAt = T0.AddHours(12), EndedAt = null } };
        var r = AvailabilityCalculator.Compute(periods, T0, T0.AddDays(1), 99.9, now: T0.AddDays(1));
        Assert.Equal(100, r.UptimePercent);
        Assert.Equal(TimeSpan.FromHours(12), r.Unknown);
        Assert.Null(r.Mttr);
    }

    [Fact]
    public void Periods_are_clipped_to_the_window()
    {
        var periods = new[] { new StatusPeriod { IsUp = false, StartedAt = T0.AddDays(-5), EndedAt = T0.AddHours(1) } };
        var r = AvailabilityCalculator.Compute(periods, T0, T0.AddHours(2), 99, now: T0.AddHours(2));
        Assert.Equal(TimeSpan.FromHours(1), r.Downtime);
    }
}

public class AlertAndRetryTests
{
    [Theory]
    [InlineData(90, ComparisonOperator.GreaterThan, 85, true)]
    [InlineData(85, ComparisonOperator.GreaterThan, 85, false)]
    [InlineData(85, ComparisonOperator.GreaterThanOrEqual, 85, true)]
    [InlineData(10, ComparisonOperator.LessThan, 14, true)]
    [InlineData(14, ComparisonOperator.LessThanOrEqual, 14, true)]
    public void Compare_operators(double value, ComparisonOperator op, double threshold, bool expected) =>
        Assert.Equal(expected, AlertEngine.Compare(value, op, threshold));

    [Fact]
    public void Health_from_worst_open_alert()
    {
        Assert.Equal(HealthStatus.Healthy, AlertEngine.HealthFrom([]));
        Assert.Equal(HealthStatus.Critical, AlertEngine.HealthFrom([
            new Alert { Fingerprint = "a", Title = "a", Severity = AlertSeverity.Warning },
            new Alert { Fingerprint = "b", Title = "b", Severity = AlertSeverity.Critical }]));
    }

    [Fact]
    public void Backoff_grows_exponentially_and_is_capped()
    {
        var normal = TimeSpan.FromSeconds(30);
        Assert.Equal(normal, RetryPolicy.BackoffInterval(normal, 0));
        Assert.Equal(normal, RetryPolicy.BackoffInterval(normal, 1));
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.BackoffInterval(normal, 2));
        Assert.Equal(TimeSpan.FromSeconds(120), RetryPolicy.BackoffInterval(normal, 3));
        Assert.Equal(TimeSpan.FromSeconds(120), RetryPolicy.BackoffInterval(normal, 20)); // capped at 4x
        Assert.Equal(TimeSpan.FromMinutes(5), RetryPolicy.BackoffInterval(TimeSpan.FromMinutes(10), 5)); // and at 5 minutes
    }

    [Fact]
    public async Task Retry_does_not_retry_authentication_failures()
    {
        var calls = 0;
        await Assert.ThrowsAsync<ServerMonitor.Infrastructure.Remote.RemoteConnectionException>(() =>
            RetryPolicy.ExecuteAsync<int>(_ =>
            {
                calls++;
                throw new ServerMonitor.Infrastructure.Remote.RemoteConnectionException(ConnectionState.AuthenticationFailed, "bad password");
            }, 3, TimeSpan.FromMilliseconds(1), CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retry_retries_transient_failures()
    {
        var calls = 0;
        var result = await RetryPolicy.ExecuteAsync(_ =>
        {
            if (++calls < 3) throw new TimeoutException();
            return Task.FromResult(42);
        }, 3, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.Equal(42, result);
        Assert.Equal(3, calls);
    }
}
