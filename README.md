![AzureWatcher dashboard](./docs/screenshot.png)

# AzureWatcher

AzureWatcher is an open-source monitoring dashboard that gives you a centralized, real-time view of your Azure ecosystem.

It automatically discovers all your Application Insights resources across all your Azure subscriptions, tracks key health metrics, and surfaces your Azure DevOps activity — all in a single interface that refreshes every 30 seconds.

## Key features

- **Multi-subscription discovery** — automatically finds all App Insights resources via Azure Resource Graph
- **Real-time health monitoring** — failed requests, error rates, and 24h exception history with visual status indicators (critical / warning / healthy)
- **Azure DevOps integration** — pipeline statuses, run timelines, open pull requests with review status, and wiki browsing with full-text search
- **Browser-based authentication** — interactive sign-in via Azure Identity with token caching; no secrets to manage locally
- **Auto-refresh** — dashboard polls every 30 seconds to stay up to date

**Built with:** .NET 10, Blazor Server, Azure SDK, Azure Resource Graph, Azure Monitor, Azure DevOps REST API

## Prerequisites

- .NET 10 SDK
- An Azure account with read access to subscriptions, AppInsights resources, and Azure DevOps

## Configuration

Local settings (organization URL, secrets) go in `appsettings.Local.json` at the project root.
This file is git-ignored.

Create the file `AzureInsightsDashboard/appsettings.Local.json`:

```json
{
  "AzureDevOps": {
    "OrganizationUrl": "https://dev.azure.com/<your-organization>"
  }
}
```

Alternatively, pass the value via the command line at startup:

```bash
dotnet run --AzureDevOps:OrganizationUrl="https://dev.azure.com/<your-organization>"
```

Or via an environment variable (`:` is replaced by `__`):

```bash
# Linux / macOS
export AzureDevOps__OrganizationUrl="https://dev.azure.com/<your-organization>"
dotnet run

# Windows PowerShell
$env:AzureDevOps__OrganizationUrl="https://dev.azure.com/<your-organization>"
dotnet run
```

## Running

```bash
cd AzureInsightsDashboard
dotnet run
```

The app starts at `https://localhost:5221`.
On the first click on "Sign in to Azure", your browser will open for Microsoft authentication.
The token is cached for the lifetime of the process.

## Required Azure permissions

| Scope               | Permission                 |
|---------------------|----------------------------|
| Azure Subscriptions | `Reader`                   |
| Azure Monitor       | `Monitoring Reader`        |
| Azure DevOps        | Organization member access |

## Wiki tab

The Wiki tab lets you browse and search all wiki pages across every project in your Azure DevOps organization.

**Browse mode**
- Lists all wikis grouped by project
- Expand a wiki to reveal the full page tree (lazy-loaded on first expand)
- Click any page to open its markdown content in a side panel
- The `↗` link in the panel header opens the page directly in Azure DevOps

**Search mode**
- Full-text search powered by the Azure DevOps Search API (`almsearch.dev.azure.com`)
- Results include highlighted snippets showing where the query matched
- Click a result to read the page content inline, with the same `↗` link to open it in Azure DevOps

> Wiki data is loaded on demand when you first navigate to the tab — it is not included in the 30-second polling loop.

## Architecture

```
Services/
  CredentialService.cs       → Singleton: holds the InteractiveBrowserCredential (MSAL)
  AzureDiscoveryService.cs   → Azure Resource Graph, multi-subscription discovery
  MetricsService.cs          → Azure Monitor Metrics API
  AzureDevOpsService.cs      → Azure DevOps REST API (pipelines, pull requests, wiki)
  DashboardService.cs        → Orchestration, 30s polling, state management

Models/
  AppInsightsResource.cs     → AppInsights models + status computation + trends
  Pipeline.cs                → Pipeline models + run history
  PullRequest.cs             → Pull request models + review status
  WikiPage.cs                → Wiki models (Wiki, WikiPageNode, WikiPageContent, WikiSearchResult)

Components/Pages/
  Dashboard.razor            → Main UI (4 tabs: Insights / Pipelines / Pull Requests / Wiki)
  WikiPageTreeNode.razor     → Recursive wiki page tree component
```

## AppInsights status computation

| Status      | Condition                                                          |
|-------------|--------------------------------------------------------------------|
| 🔴 Critical | > 50 failed requests/hour OR exception trend StrongIncrease        |
| 🟡 Warning  | > 10 failed requests/hour OR exception trend Increase              |
| 🟢 OK       | Otherwise                                                          |

## Customization

- Status thresholds: `Models/AppInsightsResource.cs` → `ComputeStatus()`
- Polling interval: `Services/DashboardService.cs` → `PollingInterval`
