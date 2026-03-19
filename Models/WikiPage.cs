namespace AzureWatcher.Models;

public class Wiki
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Project { get; set; } = "";
    public string Type { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
}

public class WikiPageNode
{
    public string Path { get; set; } = "";
    public int Order { get; set; }
    public List<WikiPageNode> SubPages { get; set; } = new();
    public string Name => Path.Split('/').LastOrDefault(p => p.Length > 0) ?? Path;
}

public class WikiPageContent
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    public string WikiId { get; set; } = "";
    public string Project { get; set; } = "";
    public string WikiName { get; set; } = "";
    public string Url { get; set; } = "";
}

public class WikiSearchResult
{
    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public string Project { get; set; } = "";
    public string WikiName { get; set; } = "";
    public string WikiId { get; set; } = "";
    public List<string> Snippets { get; set; } = new();
}

public class WikiBrowseState
{
    public Wiki Wiki { get; set; } = new();
    public List<WikiPageNode> Pages { get; set; } = new();
    public bool IsLoaded { get; set; }
    public bool IsLoading { get; set; }
}
