namespace AzureWatcher.Models;

public class PullRequest
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Project { get; set; } = "";
    public string Repository { get; set; } = "";
    public string SourceBranch { get; set; } = "";
    public string TargetBranch { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Url { get; set; } = "";
    public bool IsDraft { get; set; }
    public int ReviewerCount { get; set; }
    public int ApprovedCount { get; set; }
    public bool HasRejection { get; set; }

    public PrReviewStatus ReviewStatus => HasRejection ? PrReviewStatus.Rejected
        : ApprovedCount > 0 && ApprovedCount >= ReviewerCount ? PrReviewStatus.Approved
        : ApprovedCount > 0 ? PrReviewStatus.PartiallyApproved
        : PrReviewStatus.Waiting;
}

public enum PrReviewStatus { Waiting, PartiallyApproved, Approved, Rejected }
