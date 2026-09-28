using WpfStudio.App.Features.Inspection;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

public sealed partial class InspectionViewModelTests
{
    private static InspectionProperty SourceWritable(string token = "source-v1", bool canEdit = false) =>
        Editable(new string('a', 6000)) with
        {
            OwnerType = "System.Windows.Controls.TextBlock", SourceEditToken = token,
            CanWriteSource = true, CanEdit = canEdit
        };
    private static readonly InspectionSourcePropertyIdentity SourceIdentity = new("Text", "System.Windows.Controls.TextBlock",
        "PresentationFramework", false, "Inlines", "System.Windows.Controls.TextBlock", "PresentationFramework",
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static InspectionSourcePropertyResult SourceValidated(InspectionSourcePropertyRequest request) =>
        new(true, request.Revision, request.NodeId, request.PropertyId, request.SourceEditToken, SourceIdentity,
            request.Remove || request.VerifyOnly || request.IsNull ? null : request.Value?.Trim(), request.IsNull);
    private static FakeSession SourceEditingSession(Func<InspectionProperty>? property = null)
    {
        var session = SourceSession();
        session.Inspect = request => Task.FromResult(new InspectionElement(request.Revision, request.NodeId,
            [property?.Invoke() ?? SourceWritable(), SourceWritable() with { Name = "Tag", PropertyId = "tag-property", SourceEditToken = "tag-source" }], null, true));
        session.ValidateSource = (request, _) => Task.FromResult(SourceValidated(request));
        return session;
    }
    private static void SelectSourceProperty(InspectionViewModel model)
    {
        model.SelectedNode = model.Tree[0];
        model.SelectedProperty = model.Properties[0];
    }

    [Fact]
    public async Task SourceWriteNormalizesWithoutRuntimeMutationAndRechecksLatestRevisionAndModules()
    {
        var session = SourceEditingSession();
        int mutations = 0, temporaryValidations = 0, moduleReads = 0;
        session.Edit = _ => { mutations++; throw new InvalidOperationException("Source writing must not mutate the target."); };
        session.Validate = _ => { temporaryValidations++; throw new InvalidOperationException("Source validation is independent."); };
        session.Modules = _ => Task.FromResult(new InspectionModuleCatalog([], Status: "catalog-" + ++moduleReads));
        var validations = new List<InspectionSourcePropertyRequest>();
        session.ValidateSource = (request, _) => { validations.Add(request); return Task.FromResult(SourceValidated(request)); };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        InspectionSourceEditRequest? captured = null;
        model.SourceEditRequested += async (request, token) =>
        {
            captured = request;
            Assert.True(request.IsCurrent());
            Assert.Equal("catalog-1", request.Modules.Status);
            Assert.Equal("  12.0  ", request.Value);
            Assert.Equal("12.0", (await request.ValidateAsync(false, token)).Literal);
            int reads = session.Reads;
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.True(session.Reads > reads); // Source review does not stop live observations.
            Assert.True(request.IsCurrent());
            Assert.Equal("catalog-2", (await request.RefreshModulesAsync(token)).Status);
            Assert.True((await request.ValidateAsync(true, token)).Success);
            return new(true, "Applied reviewed XAML to editor buffer.");
        };
        await model.AttachAsync(session); SelectSourceProperty(model);
        Assert.Equal(6000, model.EditedValue.Length); // Full scalar text, never the abbreviated Value field.
        Assert.False(model.SelectedProperty!.CanEdit);
        Assert.True(model.WritePropertyToSourceCommand.CanExecute(null));
        model.EditedValue = "  12.0  ";
        await model.WritePropertyToSourceCommand.ExecuteAsync(null);
        Assert.Equal(2, validations.Count);
        Assert.All(validations, request =>
        {
            Assert.Equal("first", request.NodeId); Assert.Equal("opaque-dp", request.PropertyId);
            Assert.Equal("source-v1", request.SourceEditToken);
        });
        Assert.True(validations[1].Revision > validations[0].Revision);
        Assert.False(validations[0].VerifyOnly); Assert.True(validations[1].VerifyOnly);
        Assert.Null(validations[1].Value);
        Assert.Equal(0, mutations); Assert.Equal(0, temporaryValidations);
        Assert.Equal("Applied reviewed XAML to editor buffer.", model.SourceEditStatus);
        Assert.False(model.IsSourceEditRunning);
        Assert.False(captured!.IsCurrent()); // Review delegates expire after the operation finishes.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNullAndRemovalRemainDifferentRequests(bool remove)
    {
        var session = SourceEditingSession();
        var validations = new List<InspectionSourcePropertyRequest>();
        session.ValidateSource = (request, _) => { validations.Add(request); return Task.FromResult(SourceValidated(request)); };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        model.SourceEditRequested += async (request, token) =>
        {
            Assert.Equal(remove, request.Remove);
            Assert.Equal(!remove, request.IsNull);
            Assert.Equal(remove ? null : "unused draft", request.Value);
            await request.ValidateAsync(false, token);
            await request.ValidateAsync(true, token);
            return new(false, "Review cancelled.");
        };
        await model.AttachAsync(session); SelectSourceProperty(model);
        model.EditedValue = "unused draft"; model.EditAsNull = true;
        await (remove ? model.RemovePropertyFromSourceCommand : model.WritePropertyToSourceCommand).ExecuteAsync(null);
        Assert.Equal(2, validations.Count);
        Assert.Equal(remove, validations[0].Remove);
        Assert.Equal(!remove, validations[0].IsNull);
        Assert.Equal(remove ? null : "unused draft", validations[0].Value);
        Assert.True(validations[1].VerifyOnly); Assert.Equal(remove, validations[1].Remove);
        Assert.Null(validations[1].Value);
        Assert.Equal("Review cancelled.", model.SourceEditStatus);
    }

    [Theory]
    [InlineData("no-hint")]
    [InlineData("no-modules")]
    [InlineData("no-source-validation")]
    [InlineData("no-source-token")]
    [InlineData("unsupported")]
    [InlineData("outside-properties")]
    [InlineData("paused")]
    [InlineData("no-review-handler")]
    public async Task UnverifiedOrUnsupportedSourceContextCannotStartReview(string problem)
    {
        var property = SourceWritable();
        if (problem == "no-source-token") property = property with { SourceEditToken = null };
        if (problem == "unsupported") property = property with { CanWriteSource = false };
        var session = SourceEditingSession(() => property);
        if (problem == "no-hint") session.Nodes = [Node("first")];
        if (problem == "no-modules") session.Hello = session.Hello with { Capabilities = ["source-property-validation"] };
        if (problem == "no-source-validation") session.Hello = session.Hello with { Capabilities = ["modules"] };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int reviews = 0;
        if (problem != "no-review-handler") model.SourceEditRequested += (_, _) => { reviews++; return Task.FromResult(new InspectionSourceEditOutcome(true, "Unexpected")); };
        await model.AttachAsync(session, debugging: true); SelectSourceProperty(model);
        if (problem == "outside-properties") model.SelectedProperty = property with { PropertyId = "unobserved" };
        if (problem == "paused") model.SetDebuggerState(true);
        Assert.False(model.WritePropertyToSourceCommand.CanExecute(null));
        Assert.False(model.RemovePropertyFromSourceCommand.CanExecute(null));
        await model.WritePropertyToSourceCommand.ExecuteAsync(null);
        await model.RemovePropertyFromSourceCommand.ExecuteAsync(null);
        Assert.Equal(0, reviews); Assert.Equal(0, session.ModuleReads);
    }

    [Fact]
    public async Task SourceReviewBlocksTemporaryMutationsAndUnknownTemporaryOutcomeBlocksSourceReview()
    {
        var session = SourceEditingSession(() => SourceWritable(canEdit: true));
        var review = new TaskCompletionSource<InspectionSourceEditOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        int mutations = 0;
        session.Edit = request => { mutations++; return Task.FromResult(new InspectionPropertyEditResult(request.OperationId, "Unknown")); };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        model.SourceEditRequested += (_, _) => review.Task;
        await model.AttachAsync(session); SelectSourceProperty(model);
        var pending = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        Assert.True(model.IsSourceEditRunning);
        Assert.False(model.ApplyPropertyCommand.CanExecute(null));
        Assert.False(model.ValidatePropertyCommand.CanExecute(null));
        await model.ApplyPropertyCommand.ExecuteAsync(null);
        Assert.Equal(0, mutations);
        review.SetResult(new(false, "Cancelled")); await pending;
        await model.ApplyPropertyCommand.ExecuteAsync(null);
        Assert.Equal(1, mutations);
        Assert.False(model.WritePropertyToSourceCommand.CanExecute(null));
        Assert.False(model.RemovePropertyFromSourceCommand.CanExecute(null));
        Assert.Contains("unknown", model.SourcePropertyEditHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnchangedRefreshAndTransientGridReselectionDoNotInvalidateAnOpenSourceReview()
    {
        var session = SourceEditingSession();
        var completion = new TaskCompletionSource<InspectionSourceEditOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        InspectionSourceEditRequest? captured = null;
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        model.SourceEditRequested += (request, _) => { captured = request; return completion.Task; };
        await model.AttachAsync(session); SelectSourceProperty(model);
        model.Properties.CollectionChanged += (_, e) =>
        { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) model.SelectedProperty = null; };
        model.Tree.CollectionChanged += (_, e) =>
        { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) model.SelectedNode = null; };
        var pending = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        await model.RefreshCommand.ExecuteAsync(null); // Reseeds the unchanged, clean 6,000-character draft.
        Assert.True(captured!.IsCurrent());
        Assert.True((await captured.ValidateAsync(true, default)).Success);
        completion.SetResult(new(false, "Cancelled current review")); await pending;
        Assert.Equal("Cancelled current review", model.SourceEditStatus);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("draft-away-back")]
    [InlineData("null-away-back")]
    [InlineData("property-away-back")]
    [InlineData("node-away-back")]
    [InlineData("source-token")]
    [InlineData("pause-resume")]
    [InlineData("disconnect")]
    [InlineData("session")]
    public async Task MaterialChangesInvalidateDelayedModulesAndNeverOpenAStaleReview(string change)
    {
        var property = SourceWritable();
        var session = SourceEditingSession(() => property);
        var modules = new TaskCompletionSource<InspectionModuleCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Modules = _ => modules.Task;
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int reviews = 0;
        model.SourceEditRequested += (_, _) => { reviews++; return Task.FromResult(new InspectionSourceEditOutcome(true, "Stale source edit")); };
        await model.AttachAsync(session, debugging: true); SelectSourceProperty(model);
        var pending = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        await ChangeSourceEditContextAsync(model, change, () => property = property with { SourceEditToken = "source-v2" });
        modules.SetResult(new([])); await pending;
        Assert.Equal(0, reviews);
        Assert.DoesNotContain("Stale source edit", model.SourceEditStatus);
        Assert.False(model.IsSourceEditRunning);
    }

    [Theory]
    [InlineData("draft-away-back")]
    [InlineData("null-away-back")]
    [InlineData("property-away-back")]
    [InlineData("node-away-back")]
    [InlineData("source-token")]
    [InlineData("pause-resume")]
    [InlineData("disconnect")]
    [InlineData("session")]
    public async Task MaterialChangesWhileReviewIsOpenInvalidateItsDelegatesAndPreserveNewStatus(string change)
    {
        var property = SourceWritable();
        var session = SourceEditingSession(() => property);
        var completion = new TaskCompletionSource<InspectionSourceEditOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        InspectionSourceEditRequest? captured = null;
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        model.SourceEditRequested += (request, _) => { captured = request; return completion.Task; };
        await model.AttachAsync(session, debugging: true); SelectSourceProperty(model);
        var pending = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        await ChangeSourceEditContextAsync(model, change, () => property = property with { SourceEditToken = "source-v2" });
        Assert.False(captured!.IsCurrent());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => captured.ValidateAsync(true, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => captured.RefreshModulesAsync(default));
        model.SourceEditStatus = "Newer context status";
        completion.SetResult(new(true, "Stale edit applied")); await pending;
        Assert.Equal("Newer context status", model.SourceEditStatus);
    }

    [Fact]
    public async Task ASourceTokenChangeRequiresReloadBeforeStartingAnotherProposal()
    {
        var property = SourceWritable(); var session = SourceEditingSession(() => property);
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        model.SourceEditRequested += (_, _) => Task.FromResult(new InspectionSourceEditOutcome(false, "Cancelled"));
        await model.AttachAsync(session); SelectSourceProperty(model); model.EditedValue = "draft";
        property = property with { SourceEditToken = "source-v2" };
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("draft", model.EditedValue);
        Assert.False(model.WritePropertyToSourceCommand.CanExecute(null));
        model.ReloadPropertyValueCommand.Execute(null);
        Assert.True(model.WritePropertyToSourceCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("verify")]
    [InlineData("modules")]
    public async Task ContextIsRecheckedAfterLateSourceValidationOrModuleRefresh(string phase)
    {
        var session = SourceEditingSession();
        var validation = new TaskCompletionSource<InspectionSourcePropertyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var modules = new TaskCompletionSource<InspectionModuleCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        InspectionSourcePropertyRequest? validationRequest = null;
        session.ValidateSource = (request, _) => { validationRequest = request; return validation.Task; };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        bool reachedReview = false;
        model.SourceEditRequested += async (request, token) =>
        {
            if (phase == "modules")
            {
                session.Modules = _ => modules.Task;
                await request.RefreshModulesAsync(token);
            }
            else await request.ValidateAsync(phase == "verify", token);
            reachedReview = true;
            return new(true, "Should never apply stale response");
        };
        await model.AttachAsync(session); SelectSourceProperty(model);
        var pending = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        model.EditedValue = "newer draft";
        model.SourceEditStatus = "Newer draft status";
        if (phase == "modules") modules.SetResult(new([]));
        else validation.SetResult(SourceValidated(validationRequest!));
        await pending;
        Assert.False(reachedReview);
        Assert.Equal("Newer draft status", model.SourceEditStatus);
        Assert.False(model.IsSourceEditRunning);
    }

    [Fact]
    public async Task OldSourceCompletionCannotClearReplacementSessionOperationOrStatus()
    {
        var old = new TaskCompletionSource<InspectionSourceEditOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<InspectionSourceEditOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int calls = 0;
        model.SourceEditRequested += (_, _) => ++calls == 1 ? old.Task : current.Task;
        await model.AttachAsync(SourceEditingSession()); SelectSourceProperty(model);
        var first = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        await model.AttachAsync(SourceEditingSession()); SelectSourceProperty(model);
        var second = model.RemovePropertyFromSourceCommand.ExecuteAsync(null);
        Assert.True(model.IsSourceEditRunning);
        old.SetException(new InvalidOperationException("old session failure")); await first;
        Assert.True(model.IsSourceEditRunning);
        Assert.DoesNotContain("old session failure", model.SourceEditStatus);
        current.SetResult(new(false, "New session review cancelled")); await second;
        Assert.Equal("New session review cancelled", model.SourceEditStatus);
        Assert.False(model.IsSourceEditRunning);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("node")]
    [InlineData("property")]
    [InlineData("token")]
    [InlineData("literal")]
    public async Task ValidationAcknowledgementMustMatchTheExactSourceProposal(string mismatch)
    {
        var session = SourceEditingSession();
        session.ValidateSource = (request, _) => Task.FromResult(mismatch switch
        {
            "revision" => SourceValidated(request) with { Revision = request.Revision + 1 },
            "node" => SourceValidated(request) with { NodeId = "different" },
            "property" => SourceValidated(request) with { PropertyId = "different" },
            "token" => SourceValidated(request) with { SourceEditToken = "different" },
            _ => SourceValidated(request) with { Literal = null }
        });
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        bool accepted = false;
        model.SourceEditRequested += async (request, token) =>
        { await request.ValidateAsync(false, token); accepted = true; return new(true, "Should not apply"); };
        await model.AttachAsync(session); SelectSourceProperty(model);
        await model.WritePropertyToSourceCommand.ExecuteAsync(null);
        Assert.False(accepted);
        Assert.Contains("acknowledgement", model.SourceEditStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ChangeSourceEditContextAsync(InspectionViewModel model, string change, Action changeToken)
    {
        switch (change)
        {
            case "draft": model.EditedValue = "new draft"; break;
            case "draft-away-back":
                var text = model.EditedValue; model.EditedValue = "intermediate draft"; model.EditedValue = text; break;
            case "null-away-back": model.EditAsNull = !model.EditAsNull; model.EditAsNull = !model.EditAsNull; break;
            case "property-away-back": model.SelectedProperty = model.Properties[1]; model.SelectedProperty = model.Properties[0]; break;
            case "node-away-back":
                model.SelectedNode = model.Tree[1]; model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0]; break;
            case "source-token": changeToken(); await model.RefreshCommand.ExecuteAsync(null); break;
            case "pause-resume": model.SetDebuggerState(true); model.SetDebuggerState(false); break;
            case "disconnect": await model.DisconnectCommand.ExecuteAsync(null); break;
            case "session": await model.AttachAsync(SourceEditingSession()); SelectSourceProperty(model); break;
            default: throw new InvalidOperationException();
        }
    }
}
