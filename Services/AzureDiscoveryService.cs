using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using AzureWatcher.Models;
using System.Text.Json;

namespace AzureWatcher.Services;

public class AzureDiscoveryService(CredentialService credentialService, ILogger<AzureDiscoveryService> logger)
{
    public async Task<List<AppInsightsResource>> DiscoverAllResourcesAsync(CancellationToken ct = default)
    {
        var credential = credentialService.Get();
        var armClient = new ArmClient(credential);
        var resources = new List<AppInsightsResource>();

        // Fetch subscription names for display
        var subscriptionNames = new Dictionary<string, string>();
        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync(ct))
        {
            subscriptionNames[sub.Data.SubscriptionId!] = sub.Data.DisplayName;
        }

        if (subscriptionNames.Count == 0)
        {
            logger.LogWarning("No subscriptions found for this account.");
            return resources;
        }

        // Query all AppInsights resources via Resource Graph (single API call, all subscriptions)
        var tenant = armClient.GetTenants().GetAllAsync(ct);
        var subscriptionIds = subscriptionNames.Keys.ToList();

        var query = new ResourceQueryContent("resources | where type == 'microsoft.insights/components' | project id, name, resourceGroup, subscriptionId, location, properties")
        {
            Subscriptions = { },
            Options = new ResourceQueryRequestOptions { ResultFormat = ResultFormat.Table },
        };
        foreach (var s in subscriptionIds) query.Subscriptions.Add(s);

        var tenantResource = armClient.GetTenants().GetAllAsync(ct);
        // Use the first tenant's resource graph
        ResourceQueryResult? result = null;
        await foreach (var t in tenantResource)
        {
            result = await t.GetResourcesAsync(query, ct);
            break;
        }

        if (result?.Data is null) return resources;

        using var doc = JsonDocument.Parse(result.Data.ToObjectFromJson<object>().ToString()!);
        if (!doc.RootElement.TryGetProperty("rows", out var rows)) return resources;

        // columns: id, name, resourceGroup, subscriptionId, location, properties
        foreach (var row in rows.EnumerateArray())
        {
            var cols = row.EnumerateArray().ToList();
            if (cols.Count < 6) continue;

            var subscriptionId = cols[3].GetString() ?? "";
            var resource = new AppInsightsResource
            {
                Id = cols[0].GetString() ?? "",
                Name = cols[1].GetString() ?? "",
                ResourceGroup = cols[2].GetString() ?? "",
                SubscriptionId = subscriptionId,
                SubscriptionName = subscriptionNames.GetValueOrDefault(subscriptionId, subscriptionId),
                Location = cols[4].GetString() ?? "",
            };

            // Extract InstrumentationKey from properties
            if (cols[5].ValueKind == JsonValueKind.Object &&
                cols[5].TryGetProperty("InstrumentationKey", out var ik))
            {
                resource.InstrumentationKey = ik.GetString();
            }

            resources.Add(resource);
        }

        logger.LogInformation("Discovered {Count} Application Insights resources", resources.Count);
        return resources;
    }
}
