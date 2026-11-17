using System.Text.Json;

namespace WpfStudio.Core.DevOps.GitHub;

public sealed partial class GitHubProvider
{
    public async Task<IReadOnlyList<DevOpsPipeline>> ListPipelinesAsync(CancellationToken cancellationToken)
    {
        var workflowsTask = PagesAsync(RepoUrl("actions/workflows", ("per_page", 100)), 500, cancellationToken, "workflows");
        // One page of recent runs gives most workflows their latest run without a request per workflow.
        var runsTask = OptionalAsync(GetAsync(RepoUrl("actions/runs", ("per_page", 100)), cancellationToken));
        await Task.WhenAll(workflowsTask, runsTask).ConfigureAwait(false);
        var latest = new Dictionary<string, DevOpsRun>(StringComparer.Ordinal);
        foreach (var run in (await runsTask)?.Items("workflow_runs") ?? [])
            latest.TryAdd(run.Text("workflow_id"), Run(run));
        return (await workflowsTask).Select(workflow =>
        {
            var id = workflow.Text("id");
            var file = workflow.Str("path")?.Split('/')[^1];
            var name = workflow.Str("name") is { Length: > 0 } title ? title : file ?? id;
            if (workflow.Str("state") is { } state && state.StartsWith("disabled", StringComparison.Ordinal)) name += " (disabled)";
            return new DevOpsPipeline(id, name, file is null ? $"{Target.RepositoryWebUrl}/actions" : $"{Target.RepositoryWebUrl}/actions/workflows/{Escape(file)}",
                null, latest.GetValueOrDefault(id));
        }).OrderBy(pipeline => pipeline.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<DevOpsRun>> ListRunsAsync(RunList list, DevOpsIdentity me, string? branch, string? pipelineId, int top, CancellationToken cancellationToken)
    {
        top = Math.Clamp(top, 1, 200);
        (string, object?)[] query =
        [
            ("actor", list == RunList.Mine ? me.UniqueName ?? me.Id : null), ("branch", list == RunList.CurrentBranch ? branch : null), ("per_page", Math.Min(100, top))
        ];
        var url = pipelineId is null ? RepoUrl("actions/runs", query) : RepoUrl($"actions/workflows/{Escape(pipelineId)}/runs", query);
        var runs = await PagesAsync(url, top, cancellationToken, "workflow_runs").ConfigureAwait(false);
        return runs.Select(Run).ToList();
    }

    public async Task<DevOpsRun> GetRunAsync(DevOpsRun run, CancellationToken cancellationToken) =>
        Run(await GetAsync(RepoUrl($"actions/runs/{Escape(run.Id)}"), cancellationToken).ConfigureAwait(false));

    /// <summary>The run's jobs, each with its steps beneath it. A step's log is its job's, which GitHub keeps whole.</summary>
    public async Task<IReadOnlyList<DevOpsTimelineRecord>> GetTimelineAsync(DevOpsRun run, CancellationToken cancellationToken)
    {
        var jobs = await PagesAsync(RepoUrl($"actions/runs/{Escape(run.Id)}/jobs", ("filter", "latest"), ("per_page", 100)), 500, cancellationToken, "jobs").ConfigureAwait(false);
        var records = new List<DevOpsTimelineRecord>();
        for (var index = 0; index < jobs.Count; index++)
        {
            var job = jobs[index];
            var jobId = job.Text("id");
            var recordId = "job:" + jobId;
            records.Add(new(recordId, null, "Job", job.Text("name"), index, Status(job.Str("status")), Conclusion(job.Str("conclusion")),
                job.Date("started_at"), job.Date("completed_at"), jobId));
            foreach (var step in job.Items("steps"))
            {
                var number = step.Int("number") ?? 0;
                records.Add(new($"step:{jobId}:{number}", recordId, "Step", step.Text("name"), number, Status(step.Str("status")), Conclusion(step.Str("conclusion")),
                    step.Date("started_at"), step.Date("completed_at"), jobId));
            }
        }
        return records;
    }

    /// <summary>
    /// A job's log. GitHub answers with a redirect to a short-lived storage address, which the REST client follows without the token.
    /// </summary>
    public async Task<string> GetLogAsync(DevOpsRun run, DevOpsTimelineRecord record, CancellationToken cancellationToken)
    {
        if (record.LogId is null) return "";
        try
        {
            var response = await SendAsync(HttpMethod.Get, RepoUrl($"actions/jobs/{Escape(record.LogId)}/logs"), cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Body;
        }
        catch (DevOpsException ex) when (ex.Status is 404 or 410)
        {
            return "The log of this job is not available: it may still be running, or GitHub no longer keeps it.";
        }
    }

    /// <summary>Starts a workflow that has a <c>workflow_dispatch</c> trigger. GitHub accepts it without naming the run it starts.</summary>
    public async Task<DevOpsRun?> QueueRunAsync(string pipelineId, string branch, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(HttpMethod.Post, RepoUrl($"actions/workflows/{Escape(pipelineId)}/dispatches"), new { @ref = branch }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DevOpsException ex) when (ex.Status == 422 && ex.Message.Contains("workflow_dispatch", StringComparison.OrdinalIgnoreCase))
        {
            throw new DevOpsException("This workflow cannot be run by hand: it has no workflow_dispatch trigger.", 422, ex);
        }
        return null;
    }

    public async Task CancelRunAsync(DevOpsRun run, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, RepoUrl($"actions/runs/{Escape(run.Id)}/cancel"), cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task RetryRunAsync(DevOpsRun run, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, RepoUrl($"actions/runs/{Escape(run.Id)}/rerun-failed-jobs"), cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>Deployment reviews are answered on the web; the workbench lists none.</summary>
    public Task<IReadOnlyList<DevOpsApproval>> ListApprovalsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DevOpsApproval>>([]);

    public Task RespondToApprovalAsync(DevOpsApproval approval, bool approve, string comment, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Review GitHub deployments on the web.");

    private DevOpsRun Run(JsonElement run)
    {
        var id = run.Text("id");
        var status = Status(run.Str("status"));
        return new(id, run.Text("workflow_id"), run.Str("name") is { Length: > 0 } name ? name : "Workflow", run.Str("run_number") ?? id, status,
            Conclusion(run.Str("conclusion")), run.Str("html_url") ?? $"{Target.RepositoryWebUrl}/actions/runs/{id}",
            run.Str("head_branch"), run.Str("head_sha"), run.Get("triggering_actor")?.Str("login") ?? run.Get("actor")?.Str("login"), Reason(run.Str("event")),
            run.Date("created_at"), run.Date("run_started_at"), status == RunStatus.Completed ? run.Date("updated_at") : null);
    }

    private static RunStatus Status(string? status) => status switch
    {
        "queued" or "waiting" or "requested" or "pending" => RunStatus.Queued, "in_progress" => RunStatus.Running, "completed" => RunStatus.Completed, _ => RunStatus.Unknown
    };

    private static RunResult Conclusion(string? conclusion) => conclusion switch
    {
        "success" => RunResult.Succeeded, "failure" or "timed_out" or "startup_failure" => RunResult.Failed, "cancelled" => RunResult.Canceled,
        "skipped" => RunResult.Skipped, _ => RunResult.None
    };

    private static string? Reason(string? trigger) => trigger switch
    {
        "push" => "Push", "pull_request" or "pull_request_target" => "Pull request", "workflow_dispatch" => "Manual", "schedule" => "Scheduled",
        "release" => "Release", null or "" => null, _ => trigger
    };
}
