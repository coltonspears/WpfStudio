using System.Text;
using System.Text.Json;
using WpfStudio.Core.DevOps;
using WpfStudio.Core.DevOps.AzureDevOps;

namespace WpfStudio.Core.Tests.DevOps;

public sealed class AzureDevOpsProviderTests
{
    private const string Org = "https://dev.azure.com/contoso";
    private static readonly DevOpsIdentity Me = new("me-id", "Ada Lovelace", "ada@contoso.com");
    private static readonly DevOpsTarget Target = DevOpsTarget.FromRemoteUrl("https://dev.azure.com/contoso/Web/_git/Web.App")!;

    private static (AzureDevOpsProvider Provider, FakeHttp Http) Create(string token = "pat-123", DevOpsTarget? target = null)
    {
        var http = new FakeHttp();
        return (new AzureDevOpsProvider(http.Client(), target ?? Target, token), http);
    }

    private const string PullRequestJson = """
        {
          "pullRequestId": 42, "status": "active", "isDraft": false, "title": "Fix login", "description": "Fixes AB#7",
          "createdBy": { "id": "u1", "displayName": "Grace Hopper", "uniqueName": "grace@contoso.com" },
          "creationDate": "2026-11-01T10:00:00Z", "sourceRefName": "refs/heads/feature/7-login", "targetRefName": "refs/heads/main",
          "mergeStatus": "conflicts",
          "reviewers": [
            { "id": "me-id", "displayName": "Ada Lovelace", "vote": 10, "isRequired": true },
            { "id": "u2", "displayName": "Web Team", "vote": -5, "isContainer": true }
          ],
          "labels": [ { "name": "bug", "active": true }, { "name": "old", "active": false } ],
          "repository": { "id": "repo-guid", "name": "Web.App", "project": { "id": "project-guid", "name": "Web" } },
          "lastMergeSourceCommit": { "commitId": "aaaa1111" }, "lastMergeTargetCommit": { "commitId": "bbbb2222" },
          "autoCompleteSetBy": { "id": "me-id" }
        }
        """;

    [Fact]
    public async Task APersonalAccessTokenIsSentAsBasicAndAnEntraTokenAsBearerWithTheApiVersion()
    {
        var (provider, http) = Create("pat-123");
        http.Json("/_apis/connectionData", """{ "authenticatedUser": { "id": "me-id", "providerDisplayName": "Ada Lovelace", "properties": { "Account": { "$value": "ada@contoso.com" } } } }""");
        var me = await provider.GetCurrentUserAsync(CancellationToken.None);
        Assert.Equal(Me, me);
        var request = Assert.Single(http.Requests);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":pat-123")), request.Authorization);
        Assert.Null(request.Query("api-version"));

        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl";
        var (bearer, bearerHttp) = Create(jwt);
        bearerHttp.Json("/pullrequests", """{ "value": [] }""");
        await bearer.ListPullRequestsAsync(PullRequestList.Active, Me, 50, CancellationToken.None);
        Assert.Equal("Bearer " + jwt, bearerHttp.Requests[0].Authorization);
        Assert.Equal("7.1", bearerHttp.Requests[0].Query("api-version"));
    }

    [Fact]
    public async Task AzureDevOpsServerUsesItsOwnApiVersion()
    {
        var target = DevOpsTarget.FromRemoteUrl("https://tfs.contoso.com/tfs/DefaultCollection/Web/_git/Repo")!;
        var (provider, http) = Create(target: target);
        http.Json("/pullrequests", """{ "value": [] }""");
        await provider.ListPullRequestsAsync(PullRequestList.Active, Me, 10, CancellationToken.None);
        Assert.StartsWith("https://tfs.contoso.com/tfs/DefaultCollection/Web/_apis/git/repositories/Repo/pullrequests", http.Requests[0].Url, StringComparison.Ordinal);
        Assert.Equal("7.0", http.Requests[0].Query("api-version"));
    }

    [Fact]
    public async Task PullRequestListsSearchByCreatorOrReviewerAndMapEveryField()
    {
        var (provider, http) = Create();
        http.Json("/pullrequests", $$"""{ "value": [ {{PullRequestJson}} ] }""");
        var list = await provider.ListPullRequestsAsync(PullRequestList.Mine, Me, 25, CancellationToken.None);
        var request = http.Requests[0];
        Assert.Equal("me-id", request.Query("searchCriteria.creatorId"));
        Assert.Null(request.Query("searchCriteria.reviewerId"));
        Assert.Equal("active", request.Query("searchCriteria.status"));
        Assert.Equal("25", request.Query("$top"));

        var pr = Assert.Single(list);
        Assert.Equal(42, pr.Number);
        Assert.Equal("feature/7-login", pr.SourceBranch);
        Assert.Equal("main", pr.TargetBranch);
        Assert.Equal(PullRequestMergeState.Conflicts, pr.MergeState);
        Assert.Equal(["bug"], pr.LabelList);
        Assert.True(pr.AutoComplete);
        Assert.Equal("repo-guid", pr.RepositoryId);
        Assert.Equal("aaaa1111", pr.SourceCommit);
        Assert.Equal($"{Org}/Web/_git/Web.App/pullrequest/42", pr.WebUrl);
        Assert.Equal([ReviewVote.Approved, ReviewVote.WaitingForAuthor], pr.Reviewers.Select(r => r.Vote));
        Assert.True(pr.Reviewers[0].IsRequired);
        Assert.True(pr.Reviewers[1].Identity.IsGroup);
        Assert.Equal("Changes requested", pr.ReviewText);
        Assert.Equal("Active", pr.StatusText);

        http.Requests.Clear();
        await provider.ListPullRequestsAsync(PullRequestList.ReviewRequested, Me, 25, CancellationToken.None);
        Assert.Equal("me-id", http.Requests[0].Query("searchCriteria.reviewerId"));
        await provider.ListPullRequestsAsync(PullRequestList.Completed, Me, 25, CancellationToken.None);
        Assert.Equal("completed", http.Requests[1].Query("searchCriteria.status"));
    }

    [Fact]
    public async Task ASignInPageMeansTheTokenWasRefused()
    {
        var (provider, http) = Create();
        http.On(HttpMethod.Get, "/pullrequests", 203, "<html><title>Sign in</title></html>", "text/html");
        var error = await Assert.ThrowsAsync<DevOpsAuthenticationException>(() => provider.ListPullRequestsAsync(PullRequestList.Active, Me, 5, CancellationToken.None));
        Assert.Contains("did not accept the token for contoso", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A401WithAValidTokenNamesTheMissingScope()
    {
        var (provider, http) = Create();
        http.On(HttpMethod.Post, "/_apis/wit/wiql", 401, """{ "message": "TF400813" }""")
            .Json("/_apis/connectionData", """{ "authenticatedUser": { "id": "me-id" } }""");
        var error = await Assert.ThrowsAsync<DevOpsAccessDeniedException>(() => provider.RunQueryAsync(provider.BuiltInQueries[0], 10, CancellationToken.None));
        Assert.Equal("Work Items (Read)", error.Requirement);
        Assert.Contains("Work Items (Read)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A401WithADeadTokenIsASignInProblem()
    {
        var (provider, http) = Create();
        http.On(HttpMethod.Get, "/_apis/connectionData", 401, "")
            .On(HttpMethod.Get, "/pullrequests", 401, "");
        await Assert.ThrowsAsync<DevOpsAuthenticationException>(() => provider.ListPullRequestsAsync(PullRequestList.Active, Me, 5, CancellationToken.None));
    }

    [Fact]
    public async Task OtherFailuresCarryTheServicesMessage()
    {
        var (provider, http) = Create();
        http.On(HttpMethod.Get, "/pullrequests/9", 404, """{ "message": "TF401180: The requested pull request was not found.", "typeKey": "GitPullRequestNotFoundException" }""")
            .On(HttpMethod.Get, "/refs", 403, """{ "message": "You need the Git 'Read' permission." }""");
        var missing = await Assert.ThrowsAsync<DevOpsException>(() => provider.GetPullRequestAsync(9, CancellationToken.None));
        Assert.Equal(404, missing.Status);
        Assert.StartsWith("TF401180", missing.Message, StringComparison.Ordinal);
        var denied = await Assert.ThrowsAsync<DevOpsAccessDeniedException>(() => provider.ListBranchesAsync(CancellationToken.None));
        Assert.Contains("Git 'Read' permission", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailsReadThreadsCommitsChecksAndLinkedWorkItems()
    {
        var (provider, http) = Create();
        var pr = (await WithPullRequest(http, provider)).PullRequest;
        http.Json("/pullrequests/42/threads", """
            { "value": [
              { "id": 1, "status": "active", "threadContext": { "filePath": "/src/Login.cs", "rightFileStart": { "line": 12, "offset": 1 } },
                "comments": [ { "id": 1, "author": { "displayName": "Grace" }, "content": "Null check?", "publishedDate": "2026-11-02T10:00:00Z", "commentType": "text" },
                              { "id": 2, "author": { "displayName": "Ada" }, "content": "deleted", "isDeleted": true } ] },
              { "id": 2, "status": "fixed", "comments": [ { "id": 1, "author": { "displayName": "Ada" }, "content": "Done", "commentType": "text" } ] },
              { "id": 3, "comments": [ { "id": 1, "author": { "displayName": "Azure DevOps" }, "content": "Ada voted 10", "commentType": "system" } ] },
              { "id": 4, "isDeleted": true, "comments": [ { "id": 1, "content": "gone" } ] }
            ] }
            """)
            .Json("/pullrequests/42/commits", """{ "value": [ { "commitId": "abcdef1234", "comment": "Fix login\n\nDetails", "author": { "name": "Grace", "date": "2026-11-01T09:00:00Z" } } ] }""")
            .Json("/pullrequests/42/workitems", """{ "value": [ { "id": "7" }, { "id": "8" } ] }""")
            .Json("/pullrequests/42/statuses", """
                { "value": [
                  { "id": 1, "state": "pending", "context": { "genre": "ci", "name": "build" } },
                  { "id": 2, "state": "succeeded", "description": "Build passed", "context": { "genre": "ci", "name": "build" }, "targetUrl": "https://ci/2" }
                ] }
                """)
            .Json("/_apis/policy/evaluations", """
                { "value": [
                  { "status": "rejected", "configuration": { "isBlocking": true, "isEnabled": true, "type": { "displayName": "Minimum number of reviewers" } } },
                  { "status": "approved", "configuration": { "isEnabled": false, "type": { "displayName": "Disabled" } } },
                  { "status": "queued", "configuration": { "isBlocking": false, "isEnabled": true, "type": { "displayName": "Build" }, "settings": { "displayName": "PR build" } }, "context": { "isExpired": true } }
                ] }
                """);
        var details = await provider.GetPullRequestDetailsAsync(pr, CancellationToken.None);

        Assert.Equal(3, details.Threads.Count);
        var review = details.Threads[0];
        Assert.Equal("src/Login.cs:12", review.Location);
        Assert.Single(review.Comments);
        Assert.True(review.CanResolve);
        Assert.Equal(ThreadStatus.Resolved, details.Threads[1].Status);
        Assert.True(details.Threads[2].IsSystem);
        Assert.False(details.Threads[2].CanResolve);
        Assert.Equal("Fix login", Assert.Single(details.Commits).Subject);
        Assert.Equal([7, 8], details.WorkItemIds);
        Assert.Equal(["Minimum number of reviewers", "PR build", "ci/build"], details.Checks.Select(check => check.Name));
        Assert.Equal([CheckState.Failed, CheckState.Pending, CheckState.Succeeded], details.Checks.Select(check => check.State));
        Assert.True(details.Checks[0].IsRequired);
        Assert.Equal("Expired: queue it again", details.Checks[1].Description);
        var policy = http.Requests.Single(request => request.Url.Contains("/policy/evaluations", StringComparison.Ordinal));
        Assert.Equal("vstfs:///CodeReview/CodeReviewId/project-guid/42", policy.Query("artifactId"));
        Assert.Equal("7.1-preview.1", policy.Query("api-version"));
    }

    [Fact]
    public async Task ChecksThatCannotBeReadLeaveTheRestOfTheDetails()
    {
        var (provider, http) = Create();
        var pr = (await WithPullRequest(http, provider)).PullRequest;
        http.Json("/threads", """{ "value": [] }""").Json("/commits", """{ "value": [] }""").Json("/workitems", """{ "value": [] }""")
            .On(HttpMethod.Get, "/statuses", 403, """{ "message": "no" }""").On(HttpMethod.Get, "/policy/", 403, """{ "message": "no" }""");
        var details = await provider.GetPullRequestDetailsAsync(pr, CancellationToken.None);
        Assert.Empty(details.Checks);
    }

    [Fact]
    public async Task FilesComeFromTheNewestIterationPageByPageAndFoldersAreLeftOut()
    {
        var (provider, http) = Create();
        var pr = (await WithPullRequest(http, provider)).PullRequest;
        http.Json("/pullrequests/42/iterations/3/changes?$top=1000&$skip=0", """
                { "changeEntries": [
                  { "changeType": "edit", "item": { "path": "/src/Login.cs", "objectId": "new1", "originalObjectId": "old1" } },
                  { "changeType": "add", "item": { "path": "/src", "isFolder": true, "gitObjectType": "tree" } }
                ], "nextSkip": 2 }
                """)
            .Json("/pullrequests/42/iterations/3/changes?$top=1000&$skip=2", """
                { "changeEntries": [
                  { "changeType": "rename, edit", "item": { "path": "/src/New.cs", "objectId": "new2", "originalObjectId": "old2" }, "originalPath": "/src/Old.cs" },
                  { "changeType": "delete", "item": { "path": "/src/Gone.cs", "originalObjectId": "old3" } }
                ], "nextSkip": 0 }
                """)
            .Json("/pullrequests/42/iterations", """{ "value": [ { "id": 1 }, { "id": 3 }, { "id": 2 } ] }""");
        var files = await provider.GetPullRequestFilesAsync(pr, CancellationToken.None);
        Assert.Equal(["src/Login.cs", "src/New.cs", "src/Gone.cs"], files.Select(file => file.Path));
        Assert.Equal([FileChangeKind.Modified, FileChangeKind.Renamed, FileChangeKind.Deleted], files.Select(file => file.Change));
        Assert.Equal("src/Old.cs", files[1].OriginalPath);
        Assert.Null(files[2].HeadObjectId);
        Assert.Equal("0", http.Requests.Last().Query("$compareTo"));

        http.Json("/blobs/old1", "before\n").Json("/blobs/new1", "after\n");
        var versions = await provider.GetPullRequestFileVersionsAsync(pr, files[0], CancellationToken.None);
        Assert.Equal(("before\n", "after\n", (string?)null), (versions.Before, versions.After, versions.Message));
        Assert.Equal("octetstream", http.Requests.Last().Query("$format"));
        Assert.Contains("/project-guid/_apis/git/repositories/repo-guid/blobs/new1", http.Requests.Last().Url, StringComparison.Ordinal);

        http.Json("/blobs/old3", "MZ\0\0binary");
        var deleted = await provider.GetPullRequestFileVersionsAsync(pr, files[2], CancellationToken.None);
        Assert.Equal("Binary file — there is no text to compare.", deleted.Message);
    }

    [Fact]
    public async Task CompletingNamesTheSourceCommitItWasShownAndRefusesNewerOnes()
    {
        var (provider, http) = Create();
        var shown = (await WithPullRequest(http, provider)).PullRequest;
        http.On(HttpMethod.Patch, "/pullrequests/42", 200, PullRequestJson.Replace("\"active\"", "\"completed\"", StringComparison.Ordinal));
        var completed = await provider.CompleteAsync(shown, new(MergeStrategy.RebaseMerge, DeleteSourceBranch: true, CompleteWorkItems: false), CancellationToken.None);
        Assert.Equal(PullRequestStatus.Completed, completed.Status);
        using var body = JsonDocument.Parse(http.Single(HttpMethod.Patch, "/pullrequests/42").Body!);
        Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("aaaa1111", body.RootElement.GetProperty("lastMergeSourceCommit").GetProperty("commitId").GetString());
        var options = body.RootElement.GetProperty("completionOptions");
        Assert.Equal("rebaseMerge", options.GetProperty("mergeStrategy").GetString());
        Assert.True(options.GetProperty("deleteSourceBranch").GetBoolean());
        Assert.False(options.GetProperty("transitionWorkItems").GetBoolean());
        Assert.False(options.TryGetProperty("mergeCommitMessage", out _));

        var stale = shown with { SourceCommit = "older" };
        var error = await Assert.ThrowsAsync<DevOpsException>(() => provider.CompleteAsync(stale, new(), CancellationToken.None));
        Assert.Contains("New commits were pushed", error.Message, StringComparison.Ordinal);
        Assert.Single(http.Requests, request => request.Method == "PATCH");
    }

    [Fact]
    public async Task VotesCommentsAndStatusChangesUseThePullRequestsOwnIds()
    {
        var (provider, http) = Create();
        var pr = (await WithPullRequest(http, provider)).PullRequest;
        http.On(HttpMethod.Put, "/reviewers/me-id", 200, "{}")
            .On(HttpMethod.Post, "/threads/5/comments", 200, "{}")
            .On(HttpMethod.Post, "/threads", 200, "{}")
            .On(HttpMethod.Patch, "/threads/5", 200, "{}")
            .On(HttpMethod.Patch, "/pullrequests/42", 200, PullRequestJson);
        await provider.VoteAsync(pr, Me, ReviewVote.ApprovedWithSuggestions, "Looks good", CancellationToken.None);
        Assert.Contains("\"vote\":5", http.Single(HttpMethod.Put, "/reviewers/me-id").Body, StringComparison.Ordinal);
        Assert.Contains("/project-guid/_apis/git/repositories/repo-guid/pullrequests/42/reviewers/me-id", http.Single(HttpMethod.Put, "/reviewers/").Url, StringComparison.Ordinal);
        Assert.Contains("Looks good", http.Single(HttpMethod.Post, "/pullrequests/42/threads?").Body, StringComparison.Ordinal);

        var thread = new DevOpsThread("5", ThreadStatus.Active, [new DevOpsComment("3", Me, "first", DateTimeOffset.Now)]);
        await provider.AddCommentAsync(pr, "reply", thread, CancellationToken.None);
        Assert.Contains("\"parentCommentId\":3", http.Single(HttpMethod.Post, "/threads/5/comments").Body, StringComparison.Ordinal);
        await provider.SetThreadStatusAsync(pr, thread, ThreadStatus.Resolved, CancellationToken.None);
        Assert.Contains("\"status\":\"fixed\"", http.Single(HttpMethod.Patch, "/threads/5").Body, StringComparison.Ordinal);
        await provider.SetStatusAsync(pr, PullRequestStatus.Abandoned, CancellationToken.None);
        Assert.Contains("\"status\":\"abandoned\"", http.Requests.Last().Body, StringComparison.Ordinal);
        await provider.SetAutoCompleteAsync(pr, Me, false, new(), CancellationToken.None);
        Assert.Contains(AzureDevOpsScopes.ZeroGuid, http.Requests.Last().Body, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DevOpsException>(() => provider.AddCommentAsync(pr, "  ", null, CancellationToken.None));
    }

    [Fact]
    public async Task CreatingLinksWorkItemsAndCanSetAutoComplete()
    {
        var (provider, http) = Create();
        http.On(HttpMethod.Post, "/pullrequests?supportsIterations=true", 201, PullRequestJson)
            .On(HttpMethod.Patch, "/pullrequests/42", 200, PullRequestJson)
            .Json("/_apis/connectionData", """{ "authenticatedUser": { "id": "me-id", "providerDisplayName": "Ada" } }""");
        await provider.CreatePullRequestAsync(new("feature/7-login", "main", "Fix login", "Body", WorkItemIds: [7], AutoComplete: true), CancellationToken.None);
        using var body = JsonDocument.Parse(http.Requests.First(request => request.Method == "POST").Body!);
        Assert.Equal("refs/heads/feature/7-login", body.RootElement.GetProperty("sourceRefName").GetString());
        Assert.Equal("refs/heads/main", body.RootElement.GetProperty("targetRefName").GetString());
        Assert.Equal("7", body.RootElement.GetProperty("workItemRefs")[0].GetProperty("id").GetString());
        Assert.Contains("\"autoCompleteSetBy\":{\"id\":\"me-id\"}", http.Single(HttpMethod.Patch, "/pullrequests/42").Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuiltInWorkItemListsRunWiqlAndKeepTheQueryOrder()
    {
        var (provider, http) = Create();
        http.On(HttpMethod.Post, "/_apis/wit/wiql", 200, """{ "queryType": "flat", "workItems": [ { "id": 3 }, { "id": 1 }, { "id": 2 } ] }""")
            .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", 200, """
                { "value": [
                  { "id": 1, "fields": { "System.Title": "One", "System.WorkItemType": "Bug", "System.State": "Active", "System.TeamProject": "Web",
                    "System.AssignedTo": { "displayName": "Ada Lovelace", "uniqueName": "ada@contoso.com" }, "System.Tags": "ui; login", "Microsoft.VSTS.Common.Priority": 2 } },
                  { "id": 3, "fields": { "System.Title": "Three", "System.WorkItemType": "Task", "System.State": "New", "System.TeamProject": "Web" } }
                ] }
                """);
        var items = await provider.RunQueryAsync(provider.BuiltInQueries.Single(query => query.Name == "Assigned to me"), 500, CancellationToken.None);
        Assert.Equal([3, 1], items.Select(item => item.Id));
        Assert.Equal("Ada Lovelace", items[1].AssignedTo);
        Assert.Equal(["ui", "login"], items[1].TagList);
        Assert.Equal($"{Org}/Web/_workitems/edit/1", items[1].WebUrl);
        var wiql = http.Single(HttpMethod.Post, "/_apis/wit/wiql");
        Assert.Equal("200", wiql.Query("$top"));
        Assert.Contains("[System.AssignedTo] = @me", Wiql(wiql), StringComparison.Ordinal);
        Assert.Contains("NOT IN ('Closed', 'Done', 'Removed', 'Completed', 'Cut')", Wiql(wiql), StringComparison.Ordinal);
        Assert.Contains("[3,1,2]", http.Single(HttpMethod.Post, "/workitemsbatch").Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCurrentIterationListQuotesItsPathAndSavedTreeQueriesListEachItemOnce()
    {
        var (provider, http) = Create();
        http.Json("/teamsettings/iterations", """{ "value": [ { "path": "Web\\Sprint 'A'" } ] }""")
            .On(HttpMethod.Post, "/_apis/wit/wiql", 200, """{ "workItems": [] }""")
            .Json("/_apis/wit/wiql/q-1", """{ "queryType": "tree", "workItemRelations": [ { "target": { "id": 5 } }, { "source": { "id": 5 }, "target": { "id": 6 } } ] }""")
            .On(HttpMethod.Post, "/workitemsbatch", 200, """{ "value": [ { "id": 5, "fields": {} }, { "id": 6, "fields": {} } ] }""");
        await provider.RunQueryAsync(provider.BuiltInQueries.Single(query => query.Name == "Current iteration"), 50, CancellationToken.None);
        Assert.Contains("[System.IterationPath] = 'Web\\Sprint ''A'''", Wiql(http.Single(HttpMethod.Post, "/_apis/wit/wiql")), StringComparison.Ordinal);

        var saved = await provider.RunQueryAsync(new("q-1", "Tree", "Shared Queries", IsBuiltIn: false), 50, CancellationToken.None);
        Assert.Equal([5, 6], saved.Select(item => item.Id));
    }

    [Fact]
    public async Task SavedQueriesAreFlattenedUnderTheirFolders()
    {
        var (provider, http) = Create();
        http.Json("/_apis/wit/queries", """
            { "value": [
              { "name": "My Queries", "isFolder": true, "children": [ { "id": "q1", "name": "Bugs" } ] },
              { "name": "Shared Queries", "isFolder": true, "children": [ { "name": "Team", "isFolder": true, "children": [ { "id": "q2", "name": "Sprint" } ] } ] }
            ] }
            """);
        var queries = await provider.GetSavedQueriesAsync(CancellationToken.None);
        Assert.Equal(["My Queries / Bugs", "Shared Queries / Team / Sprint"], queries.Select(query => query.Label));
        Assert.All(queries, query => Assert.False(query.IsBuiltIn));
    }

    [Fact]
    public async Task AWorkItemReadsItsTextLinksCommentsAndStates()
    {
        var (provider, http) = Create();
        http.Json("/_apis/wit/workitems/7?", """
                { "id": 7, "rev": 12, "fields": {
                    "System.Title": "Login fails", "System.WorkItemType": "Bug", "System.State": "Active", "System.TeamProject": "Web",
                    "System.Description": "<div>Steps&nbsp;below</div><ul><li>Open</li><li>Sign in</li></ul><script>alert(1)</script>",
                    "Microsoft.VSTS.TCM.ReproSteps": "<p>Click <b>Sign in</b></p>", "Microsoft.VSTS.Scheduling.StoryPoints": 3,
                    "System.CreatedBy": { "displayName": "Grace" }, "System.CreatedDate": "2026-10-01T00:00:00Z" },
                  "relations": [
                    { "rel": "System.LinkTypes.Hierarchy-Reverse", "url": "https://dev.azure.com/contoso/_apis/wit/workItems/3" },
                    { "rel": "ArtifactLink", "url": "vstfs:///Git/PullRequestId/p%2Fr%2F42", "attributes": { "name": "Pull Request" } },
                    { "rel": "ArtifactLink", "url": "vstfs:///Git/Ref/p%2Fr%2FGBfeature%2F7-login", "attributes": { "name": "Branch" } },
                    { "rel": "Hyperlink", "url": "https://docs.example/login", "attributes": { "comment": "Spec" } }
                  ] }
                """)
            .On(HttpMethod.Post, "/workitemsbatch", 200, """{ "value": [ { "id": 3, "fields": { "System.Title": "Auth", "System.WorkItemType": "Feature", "System.State": "Active" } } ] }""")
            .Json("/workItems/7/comments", """{ "comments": [ { "id": 1, "text": "<p>Seen on <i>Edge</i></p>", "createdBy": { "displayName": "Ada" }, "createdDate": "2026-11-01T00:00:00Z" } ] }""")
            .Json("/workitemtypes/Bug/states", """{ "value": [ { "name": "New" }, { "name": "Active" }, { "name": "Resolved" }, { "name": "Closed" } ] }""");
        var details = await provider.GetWorkItemAsync(7, CancellationToken.None);
        Assert.Equal("Steps below\n\n• Open\n• Sign in", details.Description);
        Assert.DoesNotContain("alert", details.Description, StringComparison.Ordinal);
        Assert.Equal(12, details.Revision);
        Assert.Equal("Grace", details.CreatedBy);
        Assert.Equal(["Repro steps", "Story points"], details.ExtraFields.Select(field => field.Key));
        Assert.Equal("Click Sign in", details.ExtraFields[0].Value);
        Assert.Equal(["Parent", "Pull Request", "Branch", "Hyperlink"], details.Links.Select(link => link.Kind));
        Assert.Equal("Feature 3: Auth (Active)", details.Links[0].Title);
        Assert.Equal("Pull request !42", details.Links[1].Title);
        Assert.Equal("Branch feature/7-login", details.Links[2].Title);
        Assert.Equal("Spec", details.Links[3].Title);
        Assert.Equal("Seen on Edge", Assert.Single(details.Comments).Text);
        Assert.Equal(["New", "Active", "Resolved", "Closed"], details.AllowedStates);
        Assert.Equal("7.1-preview.3", http.Requests.Single(request => request.Url.Contains("/comments", StringComparison.Ordinal)).Query("api-version"));
    }

    [Fact]
    public async Task ChangingAWorkItemTestsItsRevisionFirst()
    {
        var (provider, http) = Create();
        var item = new DevOpsWorkItemDetails(new(7, "Login", "Bug", "Active", null, "https://x"), "", [], [], ["Active", "Resolved"], Revision: 12);
        http.On(HttpMethod.Patch, "/_apis/wit/workitems/7", 200, """{ "id": 7, "fields": { "System.State": "Resolved" } }""");
        var updated = await provider.SetWorkItemStateAsync(item, "Resolved", CancellationToken.None);
        Assert.Equal("Resolved", updated.State);
        var request = http.Single(HttpMethod.Patch, "/workitems/7");
        Assert.Equal("application/json-patch+json", request.ContentType);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(("test", "/rev", 12), (body.RootElement[0].GetProperty("op").GetString(), body.RootElement[0].GetProperty("path").GetString(), body.RootElement[0].GetProperty("value").GetInt32()));
        Assert.Equal("/fields/System.State", body.RootElement[1].GetProperty("path").GetString());

        var (stale, staleHttp) = Create();
        staleHttp.On(HttpMethod.Patch, "/workitems/7", 412, """{ "message": "TF26071: The test operation failed. The work item has been updated since you read it." }""");
        var error = await Assert.ThrowsAsync<DevOpsException>(() => stale.AssignWorkItemAsync(item, Me, CancellationToken.None));
        Assert.Contains("Someone changed work item 7", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewWorkItemsAndCommentsAreHtmlEncoded()
    {
        var (provider, http) = Create();
        http.On(HttpMethod.Post, "/_apis/wit/workitems/%24Bug", 200, """{ "id": 99, "fields": { "System.Title": "x", "System.WorkItemType": "Bug" } }""")
            .On(HttpMethod.Post, "/workItems/99/comments", 200, "{}");
        var created = await provider.CreateWorkItemAsync("Bug", " Crash <on> save ", "line 1\nline 2", Me, CancellationToken.None);
        Assert.Equal(99, created.Id);
        var body = http.Single(HttpMethod.Post, "/workitems/%24Bug").Body!;
        Assert.Contains("Crash <on> save", JsonDocument.Parse(body).RootElement[0].GetProperty("value").GetString(), StringComparison.Ordinal);
        Assert.Equal("line 1<br/>line 2", JsonDocument.Parse(body).RootElement[1].GetProperty("value").GetString());
        Assert.Equal("ada@contoso.com", JsonDocument.Parse(body).RootElement[2].GetProperty("value").GetString());
        await provider.AddWorkItemCommentAsync(created, "a < b", CancellationToken.None);
        Assert.Contains("a &lt; b", JsonDocument.Parse(http.Requests.Last().Body!).RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchingByNumberReadsThatItemAndByWordsMatchesTitles()
    {
        var (provider, http) = Create();
        http.On(HttpMethod.Post, "/workitemsbatch", 200, """{ "value": [ { "id": 12, "fields": { "System.Title": "Twelve" } } ] }""")
            .On(HttpMethod.Post, "/_apis/wit/wiql", 200, """{ "workItems": [ { "id": 12 } ] }""");
        Assert.Equal(12, Assert.Single(await provider.SearchWorkItemsAsync("#12", 20, CancellationToken.None)).Id);
        Assert.DoesNotContain(http.Requests, request => request.Url.Contains("/wiql", StringComparison.Ordinal));
        await provider.SearchWorkItemsAsync("login 'page'", 20, CancellationToken.None);
        Assert.Contains("[System.Title] CONTAINS 'login' AND [System.Title] CONTAINS '''page'''", Wiql(http.Single(HttpMethod.Post, "/wiql")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunsAreFilteredToTheRepositoryAndMapped()
    {
        var (provider, http) = Create();
        http.Json("/_apis/git/repositories/Web.App?", """{ "id": "repo-guid", "defaultBranch": "refs/heads/main" }""")
            .Json("/_apis/build/builds?", """
                { "value": [ { "id": 501, "buildNumber": "20261117.3", "status": "inProgress", "sourceBranch": "refs/heads/feature/x", "sourceVersion": "abc",
                  "reason": "individualCI", "requestedFor": { "displayName": "Ada" }, "definition": { "id": 9, "name": "CI" }, "startTime": "2026-11-17T10:00:00Z",
                  "_links": { "web": { "href": "https://dev.azure.com/contoso/Web/_build/results?buildId=501" } } } ] }
                """);
        var runs = await provider.ListRunsAsync(RunList.Mine, Me, "feature/x", null, 20, CancellationToken.None);
        var run = Assert.Single(runs);
        Assert.Equal(("CI #20261117.3", RunStatus.Running, "feature/x", "CI"), (run.Title, run.Status, run.Branch, run.Reason));
        Assert.True(run.IsActive);
        var request = http.Requests.Last();
        Assert.Equal("repo-guid", request.Query("repositoryId"));
        Assert.Equal("TfsGit", request.Query("repositoryType"));
        Assert.Equal("ada@contoso.com", request.Query("requestedFor"));
        Assert.Null(request.Query("branchName"));
        await provider.ListRunsAsync(RunList.CurrentBranch, Me, "feature/x", null, 20, CancellationToken.None);
        Assert.Equal("refs/heads/feature/x", http.Requests.Last().Query("branchName"));
        Assert.Equal("main", await provider.GetDefaultBranchAsync(CancellationToken.None));
        Assert.Single(http.Requests, item => item.Url.Contains("/repositories/Web.App?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TimelinesLogsAndRunActionsUseTheBuildApi()
    {
        var (provider, http) = Create();
        var run = new DevOpsRun("501", "9", "CI", "1", RunStatus.Completed, RunResult.Failed, "https://x", Branch: "main");
        http.Json("/builds/501/timeline", """
                { "records": [
                  { "id": "s", "type": "Stage", "name": "__default", "order": 1, "state": "completed", "result": "failed" },
                  { "id": "p", "parentId": "s", "type": "Phase", "name": "Job", "order": 1 },
                  { "id": "j", "parentId": "p", "type": "Job", "name": "Build", "order": 1, "state": "completed", "result": "failed", "log": { "id": 4 } },
                  { "id": "t2", "parentId": "j", "type": "Task", "name": "Test", "order": 2, "state": "completed", "result": "failed", "errorCount": 2,
                    "issues": [ { "type": "error", "message": "1 test failed" } ], "log": { "id": 6 } },
                  { "id": "t1", "parentId": "j", "type": "Task", "name": "Restore", "order": 1, "state": "completed", "result": "succeeded", "log": { "id": 5 } },
                  { "id": "c", "parentId": "s", "type": "Checkpoint", "name": "Checkpoint", "order": 0 }
                ] }
                """)
            .On(HttpMethod.Get, "/builds/501/logs/6", 200, "2026-11-17T10:00:00.1234567Z ##[error]1 test failed", "text/plain")
            .On(HttpMethod.Post, "/_apis/build/builds?", 200, """{ "id": 502, "buildNumber": "2", "status": "notStarted", "definition": { "id": 9, "name": "CI" } }""")
            .On(HttpMethod.Patch, "/builds/501", 200, "{}");
        var records = await provider.GetTimelineAsync(run, CancellationToken.None);
        var tree = PipelineTimeline.Build(records);
        var job = Assert.Single(tree);
        Assert.Equal("Build", job.Record.Name);
        Assert.Equal(["Restore", "Test"], job.Children.Select(child => child.Record.Name));
        var failure = PipelineTimeline.FirstFailure(tree)!;
        Assert.Equal(("Test", 2, "6"), (failure.Record.Name, failure.Record.Errors, failure.Record.LogId));
        Assert.Equal("1 test failed", Assert.Single(failure.Record.IssueList).Message);
        var log = await provider.GetLogAsync(run, failure.Record, CancellationToken.None);
        Assert.Equal("ERROR: 1 test failed", DevOpsText.FormatLog(log, keepTimestamps: false));
        Assert.Equal("text/plain", http.Requests.Last().Accept);

        var queued = await provider.QueueRunAsync("9", "feature/x", CancellationToken.None);
        Assert.Equal(RunStatus.Queued, queued!.Status);
        Assert.Contains("\"sourceBranch\":\"refs/heads/feature/x\"", http.Requests.Last().Body, StringComparison.Ordinal);
        Assert.Contains("\"definition\":{\"id\":9}", http.Requests.Last().Body, StringComparison.Ordinal);
        await provider.CancelRunAsync(run, CancellationToken.None);
        Assert.Contains("cancelling", http.Requests.Last().Body, StringComparison.Ordinal);
        await provider.RetryRunAsync(run, CancellationToken.None);
        Assert.Equal("true", http.Requests.Last().Query("retry"));
    }

    [Fact]
    public async Task PendingApprovalsAreListedAndAnswered()
    {
        var (provider, http) = Create();
        http.Json("/_apis/pipelines/approvals?", """
                { "value": [
                  { "id": "a1", "status": "pending", "createdOn": "2026-11-17T09:00:00Z", "instructions": "Check the release notes",
                    "steps": [ { "assignedApprover": { "displayName": "Release managers" } } ],
                    "pipeline": { "name": "Deploy", "owner": { "name": "20261117.1", "_links": { "web": { "href": "https://dev.azure.com/contoso/Web/_build/results?buildId=7" } } } } },
                  { "id": "a2", "status": "approved" }
                ] }
                """)
            .On(HttpMethod.Patch, "/_apis/pipelines/approvals", 200, "{}");
        var approval = Assert.Single(await provider.ListApprovalsAsync(CancellationToken.None));
        Assert.Equal("Deploy #20261117.1", approval.PipelineName);
        Assert.Equal(["Release managers"], approval.Approvers);
        Assert.Equal("7.1-preview.1", http.Requests[0].Query("api-version"));
        await provider.RespondToApprovalAsync(approval, approve: false, "Not today", CancellationToken.None);
        Assert.Equal("""[{"approvalId":"a1","status":"rejected","comment":"Not today"}]""", http.Requests.Last().Body);
    }

    [Fact]
    public async Task AccessIsCheckedWithoutChangingAnythingAndExplainedPerFeature()
    {
        var (provider, http) = Create();
        http.On(HttpMethod.Get, "/_apis/wit/fields/System.Title", 200, "{}")
            .On(HttpMethod.Patch, "/_apis/wit/workitems/0", 401, "")
            .On(HttpMethod.Get, "/_apis/git/repositories/" + AzureDevOpsScopes.ZeroGuid, 404, "{}")
            .On(HttpMethod.Post, $"/_apis/git/repositories/{AzureDevOpsScopes.ZeroGuid}/pullrequests", 400, "{}")
            .On(HttpMethod.Get, "/_apis/build/definitions", 401, "")
            .On(HttpMethod.Patch, "/_apis/build/builds/0", 401, "")
            .On(HttpMethod.Get, "/_apis/projects", 200, "{}");
        var entries = await provider.CheckAccessAsync(CancellationToken.None);
        var states = entries.ToDictionary(entry => entry.Feature, entry => entry.State);
        Assert.Equal(AccessState.Granted, states["View and query work items"]);
        Assert.Equal(AccessState.Denied, states["Create and edit work items"]);
        Assert.Equal(AccessState.Granted, states["Create and review pull requests"]);
        Assert.Equal(AccessState.Denied, states["View pipelines and logs"]);
        // Build (Read) is missing, so Build (Read & execute) cannot be granted either.
        Assert.Equal(AccessState.Denied, states["Run and manage pipelines"]);
        Assert.Equal("Needs Work Items (Read & write)", entries.Single(entry => entry.Feature == "Create and edit work items").StateText);
        var writes = http.Requests.Where(request => request.Method != "GET").ToList();
        Assert.All(writes, request => Assert.True(request.Url.Contains("/0?", StringComparison.Ordinal) || request.Url.Contains(AzureDevOpsScopes.ZeroGuid, StringComparison.Ordinal)));
        Assert.Contains(http.Requests, request => request.Url.Contains("validateOnly=true", StringComparison.Ordinal));
    }

    [Fact]
    public void ScopeResponsesAndTheRulesBetweenScopes()
    {
        Assert.Equal(AccessState.Denied, AzureDevOpsScopes.Classify(401, "application/json").State);
        Assert.Equal(AccessState.Denied, AzureDevOpsScopes.Classify(203, "text/html").State);
        Assert.Equal(AccessState.Denied, AzureDevOpsScopes.Classify(200, "text/html; charset=utf-8").State);
        Assert.Equal(AccessState.Granted, AzureDevOpsScopes.Classify(404, "application/json").State);
        Assert.Equal(AccessState.Granted, AzureDevOpsScopes.Classify(400, "application/json").State);
        Assert.Equal(AccessState.Unknown, AzureDevOpsScopes.Classify(500, "application/json").State);
        var normalized = AzureDevOpsScopes.Normalize(new Dictionary<string, AccessState> { [AzureDevOpsScopes.CodeWrite] = AccessState.Granted, [AzureDevOpsScopes.Build] = AccessState.Denied });
        Assert.Equal(AccessState.Granted, normalized[AzureDevOpsScopes.Code]);
        Assert.Equal(AccessState.Denied, normalized[AzureDevOpsScopes.BuildExecute]);
        Assert.Equal(AccessState.Unknown, normalized[AzureDevOpsScopes.Work]);
        var organization = AzureDevOpsUrls.ParseOrganization("contoso")!.Organization;
        Assert.DoesNotContain(AzureDevOpsScopes.Probes(organization, null, "7.1"), probe => probe.Scope == AzureDevOpsScopes.Build);
        Assert.Contains(AzureDevOpsScopes.Probes(organization, "Web", "7.1"), probe => probe.Url.StartsWith("https://vsrm.dev.azure.com/contoso/Web/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTokenIsNotSentAcrossARedirectToAnotherOrigin()
    {
        var http = new FakeHttp();
        http.On(HttpMethod.Get, "/contoso/start", _ =>
            {
                var response = FakeHttp.Response(302);
                response.Headers.Location = new Uri("https://storage.example/blob?sig=1");
                return response;
            })
            .On(HttpMethod.Get, "/contoso/same", _ =>
            {
                var response = FakeHttp.Response(307);
                response.Headers.Location = new Uri("/contoso/final", UriKind.Relative);
                return response;
            })
            .On(HttpMethod.Get, "/blob", 200, "log text", "text/plain")
            .On(HttpMethod.Get, "/contoso/final", 200, "{}");
        var organization = AzureDevOpsUrls.ParseOrganization("contoso")!.Organization;
        var client = new DevOpsRestClient(http.Client(), DevOpsRestClient.Basic("secret"), uri => AzureDevOpsUrls.IsOrganizationUrl(organization, uri));
        var log = await client.SendAsync(HttpMethod.Get, "https://dev.azure.com/contoso/start", accept: "text/plain");
        Assert.Equal("log text", log.Body);
        Assert.NotNull(http.Requests[0].Authorization);
        Assert.Null(http.Requests[1].Authorization);
        await client.SendAsync(HttpMethod.Get, "https://dev.azure.com/contoso/same");
        Assert.NotNull(http.Requests[3].Authorization);
        await Assert.ThrowsAsync<DevOpsException>(() => client.SendAsync(HttpMethod.Get, "https://dev.azure.com/fabrikam/_apis/projects"));
        Assert.Equal(4, http.Requests.Count);
    }

    private static string Wiql(RecordedRequest request)
    {
        using var body = JsonDocument.Parse(request.Body!);
        return body.RootElement.GetProperty("query").GetString()!;
    }

    private static async Task<(DevOpsPullRequest PullRequest, FakeHttp Http)> WithPullRequest(FakeHttp http, AzureDevOpsProvider provider)
    {
        http.Json("/_apis/git/pullrequests/42", PullRequestJson);
        var pr = await provider.GetPullRequestAsync(42, CancellationToken.None);
        http.Requests.Clear();
        return (pr, http);
    }
}
