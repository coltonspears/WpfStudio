using System.Globalization;
using System.Text.Json;

namespace WpfStudio.Core.DevOps.AzureDevOps;

public sealed partial class AzureDevOpsProvider
{
    public async Task<IReadOnlyList<DevOpsPipeline>> ListPipelinesAsync(CancellationToken cancellationToken)
    {
        var repositoryId = await RepositoryIdAsync(cancellationToken).ConfigureAwait(false);
        var data = await GetAsync(ProjectUrl("_apis/build/definitions", ("includeLatestBuilds", "true"), ("queryOrder", "definitionNameAscending"), ("$top", 500),
            ("repositoryId", repositoryId), ("repositoryType", repositoryId is null ? null : "TfsGit")), AzureDevOpsScopes.Build, cancellationToken).ConfigureAwait(false);
        return data.Items("value").Select(definition =>
        {
            var id = definition.Text("id");
            var folder = definition.Str("path") is { } path && path != "\\" ? path.Trim('\\') : null;
            var latest = definition.Get("latestBuild") is { } build ? Run(build) : null;
            return new DevOpsPipeline(id, definition.Text("name"), definition.Get("_links", "web", "href")?.GetString()
                ?? $"{_organization.Url}/{Escape(Target.Project)}/_build?definitionId={id}", folder, latest);
        }).ToList();
    }

    public async Task<IReadOnlyList<DevOpsRun>> ListRunsAsync(RunList list, DevOpsIdentity me, string? branch, string? pipelineId, int top, CancellationToken cancellationToken)
    {
        var repositoryId = pipelineId is null ? await RepositoryIdAsync(cancellationToken).ConfigureAwait(false) : null;
        var data = await GetAsync(ProjectUrl("_apis/build/builds", ("queryOrder", "queueTimeDescending"), ("$top", Math.Clamp(top, 1, 200)),
            ("definitions", pipelineId), ("repositoryId", repositoryId), ("repositoryType", repositoryId is null ? null : "TfsGit"),
            ("requestedFor", list == RunList.Mine ? me.UniqueName ?? me.DisplayName : null),
            ("branchName", list == RunList.CurrentBranch && branch is not null ? FullRef(branch) : null)), AzureDevOpsScopes.Build, cancellationToken).ConfigureAwait(false);
        return data.Items("value").Select(Run).ToList();
    }

    public async Task<DevOpsRun> GetRunAsync(DevOpsRun run, CancellationToken cancellationToken) =>
        Run(await GetAsync(ProjectUrl($"_apis/build/builds/{Escape(run.Id)}"), AzureDevOpsScopes.Build, cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<DevOpsTimelineRecord>> GetTimelineAsync(DevOpsRun run, CancellationToken cancellationToken)
    {
        var timeline = await GetAsync(ProjectUrl($"_apis/build/builds/{Escape(run.Id)}/timeline"), AzureDevOpsScopes.Build, cancellationToken).ConfigureAwait(false);
        return timeline.Items("records").Select(record => new DevOpsTimelineRecord(record.Text("id"), record.Str("parentId"), record.Text("type"), record.Text("name"),
            record.Int("order") ?? 0,
            record.Str("state") switch { "pending" => RunStatus.Queued, "inProgress" => RunStatus.Running, "completed" => RunStatus.Completed, _ => RunStatus.Unknown },
            Result(record.Str("result")), record.Date("startTime"), record.Date("finishTime"), record.Get("log")?.Str("id"),
            record.Int("errorCount") ?? 0, record.Int("warningCount") ?? 0,
            record.Items("issues").Select(issue => new DevOpsIssue(issue.Str("type") == "error", issue.Text("message"))).ToList())).ToList();
    }

    public async Task<string> GetLogAsync(DevOpsRun run, DevOpsTimelineRecord record, CancellationToken cancellationToken)
    {
        if (record.LogId is null) return "";
        var response = await SendAsync(HttpMethod.Get, ProjectUrl($"_apis/build/builds/{Escape(run.Id)}/logs/{Escape(record.LogId)}"), AzureDevOpsScopes.Build,
            accept: "text/plain", cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Body;
    }

    public async Task<DevOpsRun?> QueueRunAsync(string pipelineId, string branch, CancellationToken cancellationToken) =>
        Run(await JsonAsync(HttpMethod.Post, ProjectUrl("_apis/build/builds"), AzureDevOpsScopes.BuildExecute,
            new { definition = new { id = int.Parse(pipelineId, CultureInfo.InvariantCulture) }, sourceBranch = FullRef(branch) }, cancellationToken: cancellationToken).ConfigureAwait(false));

    public async Task CancelRunAsync(DevOpsRun run, CancellationToken cancellationToken) =>
        await JsonAsync(HttpMethod.Patch, ProjectUrl($"_apis/build/builds/{Escape(run.Id)}"), AzureDevOpsScopes.BuildExecute, new { status = "cancelling" }, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task RetryRunAsync(DevOpsRun run, CancellationToken cancellationToken) =>
        await JsonAsync(HttpMethod.Patch, ProjectUrl($"_apis/build/builds/{Escape(run.Id)}", ("retry", "true")), AzureDevOpsScopes.BuildExecute, new { }, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DevOpsApproval>> ListApprovalsAsync(CancellationToken cancellationToken)
    {
        var data = await GetAsync(PreviewUrl("_apis/pipelines/approvals", 1, ("state", "pending"), ("$expand", "steps")), AzureDevOpsScopes.Build, cancellationToken).ConfigureAwait(false);
        return data.Items("value").Where(approval => approval.Str("status") == "pending").Select(approval =>
        {
            var pipeline = approval.Get("pipeline");
            var name = pipeline?.Get("owner")?.Str("name") is { } run ? $"{pipeline?.Str("name")} #{run}" : pipeline?.Str("name") ?? "Pipeline";
            return new DevOpsApproval(approval.Text("id"), name, approval.Str("instructions"), approval.Date("createdOn"),
                approval.Items("steps").Select(step => step.Get("assignedApprover")?.Str("displayName")).OfType<string>().ToList(),
                pipeline?.Get("owner", "_links", "web", "href")?.GetString());
        }).ToList();
    }

    public async Task RespondToApprovalAsync(DevOpsApproval approval, bool approve, string comment, CancellationToken cancellationToken) =>
        await JsonAsync(HttpMethod.Patch, PreviewUrl("_apis/pipelines/approvals", 1), AzureDevOpsScopes.BuildExecute,
            new[] { new { approvalId = approval.Id, status = approve ? "approved" : "rejected", comment } }, cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>The repository's id, which the build filters need; null when it cannot be read, and then the lists cover the project.</summary>
    private async Task<string?> RepositoryIdAsync(CancellationToken cancellationToken)
    {
        try { return (await RepositoryAsync(cancellationToken).ConfigureAwait(false)).Str("id"); }
        catch (DevOpsAccessDeniedException) { return null; }
    }

    private DevOpsRun Run(JsonElement build)
    {
        var id = build.Text("id");
        var definition = build.Get("definition");
        var status = build.Str("status") switch
        {
            "notStarted" or "postponed" => RunStatus.Queued, "inProgress" => RunStatus.Running, "completed" => RunStatus.Completed, "cancelling" => RunStatus.Cancelling,
            _ => RunStatus.Unknown
        };
        return new(id, definition?.Str("id") ?? "", definition?.Str("name") ?? "Pipeline", build.Str("buildNumber") ?? id, status, Result(build.Str("result")),
            build.Get("_links", "web", "href")?.GetString() ?? $"{_organization.Url}/{Escape(Target.Project)}/_build/results?buildId={id}",
            ShortRef(build.Str("sourceBranch")), build.Str("sourceVersion"), build.Get("requestedFor")?.Str("displayName"), Reason(build.Str("reason")),
            build.Date("queueTime"), build.Date("startTime"), build.Date("finishTime"));
    }

    private static RunResult Result(string? result) => result switch
    {
        "succeeded" => RunResult.Succeeded, "partiallySucceeded" or "succeededWithIssues" => RunResult.PartiallySucceeded, "failed" => RunResult.Failed,
        "canceled" or "abandoned" => RunResult.Canceled, "skipped" => RunResult.Skipped, _ => RunResult.None
    };

    private static string? Reason(string? reason) => reason switch
    {
        "individualCI" or "batchedCI" => "CI", "pullRequest" => "Pull request", "manual" => "Manual", "schedule" => "Scheduled", "resourceTrigger" => "Resource trigger",
        null or "" or "none" => null, _ => reason
    };
}
