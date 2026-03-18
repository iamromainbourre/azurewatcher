using AzureWatcher.Models;

namespace AzureWatcher.Services;

public class DashboardService(
    AzureDiscoveryService discovery,
    MetricsService metrics,
    AzureDevOpsService devOps,
    ILogger<DashboardService> logger)
    : IAsyncDisposable
{
    private List<AppInsightsResource> _resources = new();
    private List<Pipeline> _allPipelines = new();
    private List<PullRequest> _pullRequests = new();
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    public event Action? OnStateChanged;

    public IReadOnlyList<AppInsightsResource> Resources => _resources;
    public IReadOnlyList<Pipeline> AllPipelines => _allPipelines;
    public int FailedPipelinesCount => _allPipelines.Count(p => p.Health == PipelineHealth.Failed);
    public IReadOnlyList<PullRequest> PullRequests => _pullRequests;
    public bool IsLoading { get; private set; }
    public bool IsAuthenticated { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset? LastRefresh { get; private set; }
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int CriticalCount => _resources.Count(r => r.Status == HealthStatus.Critical);
    public int WarningCount => _resources.Count(r => r.Status == HealthStatus.Warning);
    public int HealthyCount => _resources.Count(r => r.Status == HealthStatus.Healthy);

    public async Task InitializeAsync()
    {
        if (IsAuthenticated) return;

        IsLoading = true;
        ErrorMessage = null;
        NotifyStateChanged();

        try
        {
            // Trigger browser auth once by doing a first discovery
            _resources = await discovery.DiscoverAllResourcesAsync();
            IsAuthenticated = true;

            // Start polling loop
            _cts = new CancellationTokenSource();
            _pollingTask = RunPollingLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Authentication or discovery failed: {ex.Message}";
            logger.LogError(ex, "Init failed");
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }

    private async Task RunPollingLoopAsync(CancellationToken ct)
    {
        // First metrics fetch immediately
        await RefreshMetricsAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PollingInterval, ct);
            if (!ct.IsCancellationRequested)
                await RefreshMetricsAsync(ct);
        }
    }

    private async Task RefreshMetricsAsync(CancellationToken ct)
    {
        IsLoading = true;
        NotifyStateChanged();

        try
        {
            // Re-discover resources periodically (in case new AI resources were added)
            if (LastRefresh is null || DateTimeOffset.UtcNow - LastRefresh > TimeSpan.FromMinutes(5))
            {
                _resources = await discovery.DiscoverAllResourcesAsync(ct);
            }

            // Fetch metrics in parallel (max 10 concurrent to avoid throttling)
            var semaphore = new SemaphoreSlim(10);
            var tasks = _resources.Select(async r =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    r.Metrics = await metrics.FetchMetricsAsync(r, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            var pipelinesTask = devOps.GetAllPipelinesAsync(ct);
            var prsTask = devOps.GetActivePullRequestsAsync(ct);
            await Task.WhenAll(pipelinesTask, prsTask);
            _allPipelines = pipelinesTask.Result;
            _pullRequests = prsTask.Result;
            LastRefresh = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Metrics refresh failed");
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }

    public async Task ForceRefreshAsync()
    {
        // Reset LastRefresh to force re-discovery too
        LastRefresh = null;
        await RefreshMetricsAsync(_cts?.Token ?? CancellationToken.None);
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_pollingTask != null)
            await _pollingTask.ConfigureAwait(false);
        _cts?.Dispose();
    }
}
