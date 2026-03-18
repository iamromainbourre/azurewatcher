namespace AzureWatcher.Models;

public class FailedPipeline
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Project { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Result { get; set; } = "";
    public DateTimeOffset FinishedAt { get; set; }
    public string Url { get; set; } = "";
}
