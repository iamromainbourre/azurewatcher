namespace AzureWatcher.Models;

public class Pipeline
{
    public int DefinitionId { get; set; }
    public string Name { get; set; } = "";
    public string Project { get; set; } = "";
    public string DefinitionUrl { get; set; } = "";
    public PipelineRun? LastRun { get; set; }
    public List<PipelineRun> RecentRuns { get; set; } = new(); // last 24h, chronological

    public PipelineHealth Health => LastRun?.Result switch
    {
        "succeeded"          => PipelineHealth.Succeeded,
        "partiallySucceeded" => PipelineHealth.Partial,
        "failed"             => PipelineHealth.Failed,
        "canceled"           => PipelineHealth.Canceled,
        _                    => PipelineHealth.Unknown
    };

    public int SortOrder => Health switch
    {
        PipelineHealth.Failed    => 0,
        PipelineHealth.Partial   => 1,
        PipelineHealth.Canceled  => 2,
        PipelineHealth.Succeeded => 3,
        _                        => 4
    };
}

public class PipelineRun
{
    public string Result { get; set; } = "";
    public DateTimeOffset FinishedAt { get; set; }
    public string Url { get; set; } = "";
}

public enum PipelineHealth { Unknown, Succeeded, Partial, Canceled, Failed }
