namespace AzureWatcher.Models;

public class AppInsightsResource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public string Location { get; set; } = "";
    public string? InstrumentationKey { get; set; }
    public AppInsightsMetrics? Metrics { get; set; }
    public HealthStatus Status => ComputeStatus();

    private HealthStatus ComputeStatus()
    {
        if (Metrics == null) return HealthStatus.Unknown;
        if (Metrics.FailedRequestsLast5Min > 50 || Metrics.ExceptionTrend == Trend.StrongIncrease)
            return HealthStatus.Critical;
        if (Metrics.FailedRequestsLast5Min > 10 || Metrics.ExceptionTrend == Trend.Increase)
            return HealthStatus.Warning;
        return HealthStatus.Healthy;
    }
}

public class AppInsightsMetrics
{
    public double FailedRequestsLast5Min { get; set; }
    public double TotalRequestsLast5Min { get; set; }
    public double FailureRate => TotalRequestsLast5Min > 0
        ? FailedRequestsLast5Min / TotalRequestsLast5Min * 100
        : 0;

    // Exceptions over the last 24 intervals of 1 hour = 24h window
    public List<double> ExceptionHistory { get; set; } = new();
    public double ExceptionsLast5Min => ExceptionHistory.LastOrDefault();

    public Trend ExceptionTrend => ComputeTrend();
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    private Trend ComputeTrend()
    {
        if (ExceptionHistory.Count < 3) return Trend.Stable;

        var recent = ExceptionHistory.TakeLast(3).Average();
        var earlier = ExceptionHistory.SkipLast(3).TakeLast(3).Average();

        if (earlier == 0 && recent == 0) return Trend.Stable;
        if (earlier == 0) return recent > 0 ? Trend.Increase : Trend.Stable;

        var change = (recent - earlier) / earlier;
        return change switch
        {
            > 0.5 => Trend.StrongIncrease,
            > 0.1 => Trend.Increase,
            < -0.1 => Trend.Decrease,
            _ => Trend.Stable
        };
    }
}

public enum HealthStatus { Unknown, Healthy, Warning, Critical }
public enum Trend { Decrease, Stable, Increase, StrongIncrease }
