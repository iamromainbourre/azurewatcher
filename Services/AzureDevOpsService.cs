using Azure.Core;
using AzureWatcher.Models;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzureWatcher.Services;

public class AzureDevOpsService(
    CredentialService credentialService,
    ILogger<AzureDevOpsService> logger,
    IConfiguration config)
{
    private readonly string _orgUrl = config["AzureDevOps:OrganizationUrl"]?.TrimEnd('/') ?? throw new ArgumentException("AzureDevOps organization URL cannot be null or empty");
    private readonly string _orgName = ExtractOrgName(config["AzureDevOps:OrganizationUrl"]?.TrimEnd('/') ?? "");

    private static string ExtractOrgName(string orgUrl)
    {
        if (string.IsNullOrEmpty(orgUrl)) return "";
        try
        {
            var uri = new Uri(orgUrl);
            if (uri.Host.EndsWith("visualstudio.com", StringComparison.OrdinalIgnoreCase))
                return uri.Host.Split('.')[0];
            return uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    // Azure DevOps resource ID for token acquisition
    private static readonly string[] DevOpsScopes = ["499b84ac-1321-427f-aa17-267ca6975798/.default"];

    private static string SanitizeWikiSearchSnippet(string snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return "";

        var sanitized = System.Net.WebUtility.HtmlEncode(snippet);
        return sanitized
            .Replace("&lt;c0&gt;", "<mark>", StringComparison.Ordinal)
            .Replace("&lt;/c0&gt;", "</mark>", StringComparison.Ordinal);
    }

    public async Task<List<Pipeline>> GetAllPipelinesAsync(CancellationToken ct = default)
    {
        var result = new List<Pipeline>();
        try
        {
            var credential = credentialService.Get();
            var token = await credential.GetTokenAsync(new TokenRequestContext(DevOpsScopes), ct);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var projects = await GetProjectsAsync(http, ct);
            var tasks = projects.Select(p => FetchProjectPipelinesAsync(http, p, ct));
            var all = await Task.WhenAll(tasks);
            result = all.SelectMany(x => x).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch all pipelines");
        }
        return result;
    }

    private async Task<List<Pipeline>> FetchProjectPipelinesAsync(HttpClient http, string project, CancellationToken ct)
    {
        var pipelines = new Dictionary<int, Pipeline>();
        try
        {
            // 1. All pipeline definitions
            var defsUrl = $"{_orgUrl}/{Uri.EscapeDataString(project)}/_apis/build/definitions?api-version=7.1&$top=200";
            var defsResp = await http.GetAsync(defsUrl, ct);
            defsResp.EnsureSuccessStatusCode();

            using var defsDoc = JsonDocument.Parse(await defsResp.Content.ReadAsStringAsync(ct));
            foreach (var def in defsDoc.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = def.GetProperty("id").GetInt32();
                var webUrl = def.TryGetProperty("_links", out var lnk) &&
                             lnk.TryGetProperty("web", out var w) &&
                             w.TryGetProperty("href", out var h)
                    ? h.GetString() ?? ""
                    : $"{_orgUrl}/{Uri.EscapeDataString(project)}/_build/definition?definitionId={id}";

                pipelines[id] = new Pipeline
                {
                    DefinitionId = id,
                    Name = def.GetProperty("name").GetString() ?? "",
                    Project = project,
                    DefinitionUrl = webUrl,
                };
            }

            // 2. All completed builds from the last 24h
            var minTime = DateTimeOffset.UtcNow.AddHours(-24).ToString("o");
            var buildsUrl = $"{_orgUrl}/{Uri.EscapeDataString(project)}/_apis/build/builds" +
                            $"?statusFilter=completed&minTime={Uri.EscapeDataString(minTime)}&$top=500&api-version=7.1";
            var buildsResp = await http.GetAsync(buildsUrl, ct);
            buildsResp.EnsureSuccessStatusCode();

            var recentByDef = new Dictionary<int, List<PipelineRun>>();
            using var buildsDoc = JsonDocument.Parse(await buildsResp.Content.ReadAsStringAsync(ct));
            foreach (var b in buildsDoc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (!b.TryGetProperty("definition", out var defEl)) continue;
                var defId = defEl.GetProperty("id").GetInt32();
                if (!b.TryGetProperty("result", out var resEl)) continue;
                var res = resEl.GetString() ?? "";
                if (string.IsNullOrEmpty(res)) continue;

                var finishTime = b.TryGetProperty("finishTime", out var ft)
                    ? DateTimeOffset.Parse(ft.GetString()!) : DateTimeOffset.UtcNow;
                var runUrl = b.TryGetProperty("_links", out var rl) && rl.TryGetProperty("web", out var rw) && rw.TryGetProperty("href", out var rh)
                    ? rh.GetString() ?? "" : "";

                if (!recentByDef.ContainsKey(defId)) recentByDef[defId] = new();
                recentByDef[defId].Add(new PipelineRun { Result = res, FinishedAt = finishTime, Url = runUrl });
            }

            foreach (var (defId, runs) in recentByDef)
            {
                if (!pipelines.TryGetValue(defId, out var p)) continue;
                var sorted = runs.OrderBy(r => r.FinishedAt).ToList();
                p.RecentRuns = sorted;
                p.LastRun = sorted.Last();
            }

            // 3. Batch-fetch last run for pipelines with no activity in 24h
            var missingIds = pipelines.Keys.Where(id => !recentByDef.ContainsKey(id)).ToList();
            if (missingIds.Any())
            {
                var idList = string.Join(",", missingIds);
                var lastUrl = $"{_orgUrl}/{Uri.EscapeDataString(project)}/_apis/build/builds" +
                              $"?definitions={idList}&statusFilter=completed&$top={missingIds.Count * 2}&queryOrder=queueTimeDescending&api-version=7.1";
                try
                {
                    var lastResp = await http.GetAsync(lastUrl, ct);
                    lastResp.EnsureSuccessStatusCode();
                    var seen = new HashSet<int>();
                    using var lastDoc = JsonDocument.Parse(await lastResp.Content.ReadAsStringAsync(ct));
                    foreach (var b in lastDoc.RootElement.GetProperty("value").EnumerateArray())
                    {
                        if (!b.TryGetProperty("definition", out var defEl)) continue;
                        var defId = defEl.GetProperty("id").GetInt32();
                        if (!seen.Add(defId)) continue;
                        if (!b.TryGetProperty("result", out var resEl)) continue;
                        var res = resEl.GetString() ?? "";
                        if (string.IsNullOrEmpty(res)) continue;
                        var finishTime = b.TryGetProperty("finishTime", out var ft)
                            ? DateTimeOffset.Parse(ft.GetString()!) : DateTimeOffset.UtcNow;
                        var runUrl = b.TryGetProperty("_links", out var rl) && rl.TryGetProperty("web", out var rw) && rw.TryGetProperty("href", out var rh)
                            ? rh.GetString() ?? "" : "";
                        if (pipelines.TryGetValue(defId, out var p))
                            p.LastRun = new PipelineRun { Result = res, FinishedAt = finishTime, Url = runUrl };
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to fetch last runs for {Project}", project);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch pipelines for {Project}", project);
        }

        return pipelines.Values.Where(p => p.LastRun != null).ToList();
    }

    private async Task<List<string>> GetProjectsAsync(HttpClient http, CancellationToken ct)
    {
        var url = $"{_orgUrl}/_apis/projects?api-version=7.1&$top=100";
        var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(p => p.GetProperty("name").GetString() ?? "")
            .Where(n => n.Length > 0)
            .ToList();
    }

    public async Task<List<PullRequest>> GetActivePullRequestsAsync(CancellationToken ct = default)
    {
        var result = new List<PullRequest>();
        try
        {
            var credential = credentialService.Get();
            var token = await credential.GetTokenAsync(new TokenRequestContext(DevOpsScopes), ct);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var projects = await GetProjectsAsync(http, ct);
            var tasks = projects.Select(p => FetchActivePrsAsync(http, p, ct));
            var all = await Task.WhenAll(tasks);
            result = all.SelectMany(x => x).OrderBy(p => p.CreatedAt).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch Azure DevOps pull requests");
        }
        return result;
    }

    private async Task<List<PullRequest>> FetchActivePrsAsync(HttpClient http, string project, CancellationToken ct)
    {
        var result = new List<PullRequest>();
        try
        {
            var url = $"{_orgUrl}/{Uri.EscapeDataString(project)}/_apis/git/pullrequests" +
                      $"?searchCriteria.status=active&$top=50&api-version=7.1";

            var resp = await http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var pr in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var webUrl = pr.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                // Convert API url to web url
                webUrl = $"{_orgUrl}/{Uri.EscapeDataString(project)}/_git/" +
                         $"{(pr.TryGetProperty("repository", out var repo) ? Uri.EscapeDataString(repo.GetProperty("name").GetString() ?? "") : "")}" +
                         $"/pullrequest/{pr.GetProperty("pullRequestId").GetInt32()}";

                var reviewers = pr.TryGetProperty("reviewers", out var revs)
                    ? revs.EnumerateArray().ToList()
                    : new List<JsonElement>();

                var approvedCount = reviewers.Count(r => r.TryGetProperty("vote", out var v) && v.GetInt32() >= 10);
                var hasRejection = reviewers.Any(r => r.TryGetProperty("vote", out var v) && v.GetInt32() <= -10);

                result.Add(new PullRequest
                {
                    Id = pr.GetProperty("pullRequestId").GetInt32(),
                    Title = pr.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    Project = project,
                    Repository = pr.TryGetProperty("repository", out var r2) ? r2.GetProperty("name").GetString() ?? "" : "",
                    SourceBranch = pr.TryGetProperty("sourceRefName", out var src) ? src.GetString()?.Replace("refs/heads/", "") ?? "" : "",
                    TargetBranch = pr.TryGetProperty("targetRefName", out var tgt) ? tgt.GetString()?.Replace("refs/heads/", "") ?? "" : "",
                    AuthorName = pr.TryGetProperty("createdBy", out var author) ? author.GetProperty("displayName").GetString() ?? "" : "",
                    CreatedAt = pr.TryGetProperty("creationDate", out var cd) ? DateTimeOffset.Parse(cd.GetString()!) : DateTimeOffset.UtcNow,
                    Url = webUrl,
                    IsDraft = pr.TryGetProperty("isDraft", out var draft) && draft.GetBoolean(),
                    ReviewerCount = reviewers.Count(r => r.TryGetProperty("vote", out var v) && v.GetInt32() != 0),
                    ApprovedCount = approvedCount,
                    HasRejection = hasRejection,
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch PRs for project {Project}", project);
        }
        return result;
    }

    // ── Wiki ──────────────────────────────────────────────────────────────

    public async Task<List<WikiBrowseState>> GetAllWikisAsync(CancellationToken ct = default)
    {
        var result = new List<WikiBrowseState>();
        try
        {
            var credential = credentialService.Get();
            var token = await credential.GetTokenAsync(new TokenRequestContext(DevOpsScopes), ct);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var projects = await GetProjectsAsync(http, ct);
            var tasks = projects.Select(p => GetProjectWikisAsync(http, p, ct));
            var all = await Task.WhenAll(tasks);
            result = all.SelectMany(x => x)
                        .Select(w => new WikiBrowseState { Wiki = w })
                        .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch all wikis");
        }
        return result;
    }

    private async Task<List<Wiki>> GetProjectWikisAsync(HttpClient http, string project, CancellationToken ct)
    {
        var result = new List<Wiki>();
        try
        {
            var url = $"{_orgUrl}/{Uri.EscapeDataString(project)}/_apis/wiki/wikis?api-version=7.1";
            var resp = await http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var w in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                result.Add(new Wiki
                {
                    Id = w.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Name = w.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Project = project,
                    Type = w.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                    RemoteUrl = w.TryGetProperty("remoteUrl", out var remoteUrl) ? remoteUrl.GetString() ?? "" : "",
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch wikis for {Project}", project);
        }
        return result;
    }

    public async Task<List<WikiPageNode>> GetWikiPagesAsync(string project, string wikiId, CancellationToken ct = default)
    {
        try
        {
            var credential = credentialService.Get();
            var token = await credential.GetTokenAsync(new TokenRequestContext(DevOpsScopes), ct);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var url = $"{_orgUrl}/{Uri.EscapeDataString(project)}/_apis/wiki/wikis/{Uri.EscapeDataString(wikiId)}/pages" +
                      $"?path=/&recursionLevel=full&api-version=7.1";
            var resp = await http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = ParseWikiPageNode(doc.RootElement);
            return root.SubPages.Any() ? root.SubPages : new List<WikiPageNode> { root };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch wiki pages for {WikiId}", wikiId);
            return new List<WikiPageNode>();
        }
    }

    private static WikiPageNode ParseWikiPageNode(JsonElement element)
    {
        var node = new WikiPageNode
        {
            Path = element.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/",
            Order = element.TryGetProperty("order", out var o) ? o.GetInt32() : 0,
        };

        if (element.TryGetProperty("subPages", out var subs) && subs.ValueKind == JsonValueKind.Array)
        {
            foreach (var sub in subs.EnumerateArray())
                node.SubPages.Add(ParseWikiPageNode(sub));
        }

        return node;
    }

    public async Task<WikiPageContent?> GetWikiPageContentAsync(
        string project, string wikiId, string wikiName, string pagePath, CancellationToken ct = default)
    {
        try
        {
            var credential = credentialService.Get();
            var token = await credential.GetTokenAsync(new TokenRequestContext(DevOpsScopes), ct);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var url = $"{_orgUrl}/{Uri.EscapeDataString(project)}/_apis/wiki/wikis/{Uri.EscapeDataString(wikiId)}/pages" +
                      $"?path={Uri.EscapeDataString(pagePath)}&includeContent=true&api-version=7.1";
            var resp = await http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return new WikiPageContent
            {
                Path = pagePath,
                Content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                WikiId = wikiId,
                Project = project,
                WikiName = wikiName,
                Url = $"{_orgUrl}/{Uri.EscapeDataString(project)}/_wiki/wikis/{Uri.EscapeDataString(wikiName)}?pagePath={Uri.EscapeDataString(pagePath)}",
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch wiki page content for {Path}", pagePath);
            return null;
        }
    }

    public async Task<List<WikiSearchResult>> SearchWikiAsync(string searchText, CancellationToken ct = default)
    {
        var result = new List<WikiSearchResult>();
        try
        {
            var credential = credentialService.Get();
            var token = await credential.GetTokenAsync(new TokenRequestContext(DevOpsScopes), ct);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var searchUrl = $"https://almsearch.dev.azure.com/{Uri.EscapeDataString(_orgName)}/_apis/search/wikisearchresults?api-version=7.1-preview.1";
            var bodyJson = new JsonObject
            {
                ["searchText"] = searchText,
                ["$top"] = 50,
                ["$skip"] = 0,
                ["includeFacets"] = false
            }.ToJsonString();

            var content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.PostAsync(searchUrl, content, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("results", out var results)) return result;

            foreach (var r in results.EnumerateArray())
            {
                string projectName = "";
                if (r.TryGetProperty("project", out var projEl))
                    projectName = projEl.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";

                string wikiName = "", wikiId = "";
                if (r.TryGetProperty("wiki", out var wikiEl))
                {
                    wikiName = wikiEl.TryGetProperty("name", out var wn) ? wn.GetString() ?? "" : "";
                    wikiId = wikiEl.TryGetProperty("id", out var wi) ? wi.GetString() ?? "" : "";
                }

                var snippets = new List<string>();
                if (r.TryGetProperty("hits", out var hits))
                {
                    foreach (var hit in hits.EnumerateArray())
                    {
                        if (hit.TryGetProperty("charContent", out var cc))
                        {
                            var snippet = cc.GetString() ?? "";
                            snippet = SanitizeWikiSearchSnippet(snippet);
                            snippets.Add(snippet);
                        }
                    }
                }

                result.Add(new WikiSearchResult
                {
                    FileName = r.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "",
                    Path = r.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "",
                    Project = projectName,
                    WikiName = wikiName,
                    WikiId = wikiId,
                    Snippets = snippets,
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search wiki");
        }
        return result;
    }

}
