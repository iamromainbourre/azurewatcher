using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using AzureWatcher.Models;

namespace AzureWatcher.Services;

public class MetricsService(CredentialService credentialService, ILogger<MetricsService> logger)
{
    public async Task<AppInsightsMetrics> FetchMetricsAsync(AppInsightsResource resource, CancellationToken ct = default)
    {
        var metrics = new AppInsightsMetrics();

        try
        {
            var client = new MetricsQueryClient(credentialService.Get());
            var timespan = new QueryTimeRange(TimeSpan.FromHours(24));

            var response = await client.QueryResourceAsync(
                resource.Id,
                new[]
                {
                    "requests/failed",
                    "requests/count",
                    "exceptions/count"
                },
                new MetricsQueryOptions
                {
                    TimeRange = timespan,
                    Granularity = TimeSpan.FromHours(1),
                    Aggregations = { MetricAggregationType.Total, MetricAggregationType.Count }
                },
                ct);

            var results = response.Value.Metrics;

            metrics.FailedRequestsLast5Min = GetLastValue(results, "requests/failed");
            metrics.TotalRequestsLast5Min = GetLastValue(results, "requests/count");
            metrics.ExceptionHistory = GetTimeSeries(results, "exceptions/count");
            metrics.LastUpdated = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch metrics for {Resource}", resource.Name);
        }

        return metrics;
    }

    private static double GetLastValue(IReadOnlyList<MetricResult> metrics, string name)
    {
        var metric = metrics.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (metric is null) return 0;

        var lastTs = metric.TimeSeries
            .SelectMany(ts => ts.Values)
            .OrderByDescending(v => v.TimeStamp)
            .FirstOrDefault();

        return lastTs?.Total ?? lastTs?.Average ?? 0;
    }

    private static List<double> GetTimeSeries(IReadOnlyList<MetricResult> metrics, string name)
    {
        var metric = metrics.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (metric is null) return new List<double>();

        return metric.TimeSeries
            .SelectMany(ts => ts.Values)
            .OrderBy(v => v.TimeStamp)
            .Select(v => v.Total ?? v.Average ?? (double?)(long?)v.Count ?? 0)
            .ToList();
    }
}
