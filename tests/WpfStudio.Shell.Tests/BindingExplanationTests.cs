using WpfStudio.App.Features.BindingDiagnostics;
using WpfStudio.App.Features.BindingSources;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Inspection;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

internal static class BindingExplanationTestData
{
    internal static InspectionBinding Observation(string status = "PathError") => new("Child.Name", status, status, "Current observed status: " + status,
        "Demo.Model", "Demo.Child", "Name", Details: new("DataContextOrNullSource", "OneWay", "Demo.Converter",
            "No source getter was evaluated. Historical evidence remains historical.", [],
            [new(1, DateTimeOffset.UnixEpoch, "MissingMember", 40, "Earlier missing property", "Name", "Demo.OldChild", "Demo.Model", "PathError", true)],
            PathState: new(true, "Cached", [new(0, "Property", "Child", "Resolved", "Demo.Model"),
                new(1, "Property", "Name", "Unresolved")], 1, UnavailableReason: "WPF no longer retains the failed owner; null versus missing is not established.")));

    internal static BindingSourcesSnapshot WithObservations(BindingSourcesSnapshot sources, string status = "PathError") => sources with
    {
        Declarations = sources.Declarations.Select(declaration => declaration with
        { Observation = Observation(status) with { Path = declaration.Path }, Status = status }).ToArray()
    };
}

public sealed class BindingExplanationPresentationTests
{
    [Fact]
    public void CurrentPathQualificationAndHistoryRemainDistinctAndClearTogether()
    {
        var vm = new BindingExplanationViewModel();
        var source = BindingExplanationTestData.WithObservations(BindingNavigationTestData.Single("file:///C:/View.xaml"));
        var item = new BindingDeclarationItem("Text", "TextBlock", "PresentationFramework", "property", source.BindingId, source.Declarations[0]);
        vm.Apply(item, item.Declaration.Observation, "fallback", "Local");
        Assert.True(vm.Available);
        Assert.Contains("null versus missing", vm.PathStatus);
        Assert.Contains("First unresolved step: 2", vm.PathStatus);
        Assert.StartsWith("2. Name", vm.Segments[1].Label);
        Assert.Equal("Demo.OldChild", Assert.Single(vm.Evidence).OwnerType);
        Assert.DoesNotContain("OldChild", vm.Observation!.Explanation);
        vm.Clear("Paused");
        Assert.False(vm.Available);
        Assert.Null(vm.Observation);
        Assert.Empty(vm.Segments); Assert.Empty(vm.Evidence);
    }

    [Fact]
    public void PendingAndUnavailableDetailsDoNotInventPathRowsOrCurrentCauses()
    {
        var source = BindingNavigationTestData.Single("file:///C:/View.xaml");
        var item = new BindingDeclarationItem("Text", "TextBlock", "PresentationFramework", null, source.BindingId, source.Declarations[0]);
        var vm = new BindingExplanationViewModel();
        var observation = BindingExplanationTestData.Observation("AsyncRequestPending");
        vm.Apply(item, observation with { Details = observation.Details! with { PathState = new(true, "NotObserved", [], UnavailableReason: "WPF transfer is pending.") } }, "", "Local");
        Assert.Contains("pending", vm.PathStatus); Assert.Empty(vm.Segments);
        vm.Apply(item, null, "", "Local");
        Assert.False(vm.Available); Assert.Contains("host", vm.Status);
        Assert.Empty(vm.Evidence);
    }
}

public sealed partial class DesignerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewPickOverlappingSourceValidationCannotRestoreOldBindingDetails(bool inspectionFinishesFirst)
    {
        var client = ExplanationPreview();
        var document = Document("Binding.xaml");
        var source = new SourceLocation(document.Path, 0, document.Content.Length, 1, 1);
        client.Render = request => Task.FromResult(Snapshot(request.Version, source: source));
        var inspect = client.Inspect;
        client.Inspect = async request =>
        {
            var response = await inspect(request);
            return response with { Node = Node(source: source), Properties = response.Properties.Select(property =>
                property with { CanWriteSource = property.Name == "Text" }).ToArray() };
        };
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        model.SourceEditRequested += _ => Task.FromResult(false);
        await model.OpenAsync(document); model.SelectedNode = model.Tree[0]; model.SelectedProperty = model.Properties[0];
        model.EditedValue = "Unsubmitted draft";
        var pendingValidation = new TaskCompletionSource<PreviewPropertyValidation>();
        var pendingPick = new TaskCompletionSource<PreviewInspection>();
        client.Validate = _ => pendingValidation.Task;
        client.Pick = _ => pendingPick.Task;
        Assert.True(model.WritePropertyToSourceCommand.CanExecute(null));
        var reviewing = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        var picking = model.PickAsync(1, 1);
        Assert.False(model.BindingExplanation.Available);
        if (!inspectionFinishesFirst) { pendingValidation.SetResult(new(false, "Validation rejected")); await reviewing; }
        var picked = await client.Inspect(new(client.Requests.Last().Version, "1"));
        pendingPick.SetResult(picked with { Properties = picked.Properties.Select(property => property with { Value = "New observed value" }).ToArray() });
        await picking;
        if (inspectionFinishesFirst) { pendingValidation.SetResult(new(false, "Validation rejected")); await reviewing; }
        Assert.False(model.BindingExplanation.Available);
        Assert.Equal("New observed value", model.SelectedProperty!.Value);
        Assert.Equal("Unsubmitted draft", model.EditedValue);
        await model.UpdateSnapshotCommand.ExecuteAsync(null);
        Assert.True(model.BindingExplanation.Available);
        Assert.Equal("Unsubmitted draft", model.EditedValue);
    }

    private static FakePreview ExplanationPreview()
    {
        var client = BindingSourcePreview(Path.Combine(Path.GetTempPath(), "Binding.xaml"));
        var inspect = client.Inspect;
        client.Inspect = async request =>
        {
            var result = await inspect(request);
            return result with { Properties = result.Properties.Select(property => property with
            { Binding = BindingExplanationTestData.Observation(), BindingSources = BindingExplanationTestData.WithObservations(property.BindingSources!) }).ToArray() };
        };
        return client;
    }

    [Fact]
    public async Task PreviewChildDiagnosisAndSnapshotRefreshPreserveThePropertyDraft()
    {
        var client = ExplanationPreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("Binding.xaml")); model.SelectedNode = model.Tree[0];
        model.SelectedProperty = model.Properties[0]; model.EditedValue = "Unsubmitted draft";
        model.SelectedBindingDeclaration = model.BindingDeclarations.Single(item => item.Declaration.ExpressionId == "second-expression");
        Assert.Equal("Second", model.BindingExplanation.Observation!.Path);
        Assert.Equal("Tag", model.SelectedBindingDeclaration.Property);
        await model.UpdateSnapshotCommand.ExecuteAsync(null);
        Assert.Equal("second-expression", model.SelectedBindingDeclaration!.Declaration.ExpressionId);
        Assert.Equal("Second", model.BindingExplanation.Observation!.Path);
        Assert.Equal("Text", model.SelectedProperty!.Name);
        Assert.Equal("Unsubmitted draft", model.EditedValue);
        client.Exit("Host stopped");
        Assert.False(model.BindingExplanation.Available);
    }

    [Fact]
    public async Task PreviewDiagnosticSelectsExactRootAndRecoveredInspectionRemovesOnlyCurrentErrors()
    {
        var current = new PreviewDiagnostic("Current binding failure", NodeId: "1", Property: "Text", BindingId: "text-expression");
        var historical = new PreviewDiagnostic("Historical WPF binding trace: earlier failure", "Warning");
        var client = ExplanationPreview();
        client.Render = request => Task.FromResult(Snapshot(request.Version) with { Diagnostics = [current, historical] });
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("Binding.xaml")); model.SelectedNode = model.Tree[0];
        // Re-introduce the snapshot's failure for the diagnostic action, then make
        // the ensuing observation a verified success of the same expression.
        model.Diagnostics.Add(current);
        var inspect = client.Inspect;
        client.Inspect = async request =>
        {
            var result = await inspect(request);
            return result with { Properties = result.Properties.Select(property => property with
            { Binding = BindingExplanationTestData.Observation("Active"), BindingSources = BindingExplanationTestData.WithObservations(property.BindingSources!, "Active") }).ToArray() };
        };
        model.SelectedProperty = model.Properties[1]; model.EditedValue = "Keep Tag draft";
        await model.ShowBindingDiagnosticCommand.ExecuteAsync(current);
        Assert.Equal("text-expression", model.SelectedBindingDeclaration!.BindingId);
        Assert.Equal("Active", model.BindingExplanation.Observation!.Status);
        Assert.Single(model.BindingExplanation.Evidence);
        Assert.DoesNotContain(current, model.Diagnostics); Assert.Contains(historical, model.Diagnostics);
        Assert.Equal("Tag", model.SelectedProperty!.Name); Assert.Equal("Keep Tag draft", model.EditedValue);
        Assert.False(model.ShowBindingDiagnosticCommand.CanExecute(historical));
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("selection")]
    [InlineData("source")]
    public async Task PreviewDiagnosticCannotSelectAReplacementOrOverrideLaterInput(string change)
    {
        var issue = new PreviewDiagnostic("Current error", NodeId: "1", Property: "Text", BindingId: "text-expression");
        var client = ExplanationPreview();
        client.Render = request => Task.FromResult(Snapshot(request.Version) with { Diagnostics = [issue] });
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("Binding.xaml");
        await model.OpenAsync(document); model.SelectedNode = model.Tree[0]; model.Diagnostics.Add(issue);
        var inspect = client.Inspect;
        var pending = new TaskCompletionSource<PreviewInspection>();
        client.Inspect = _ => pending.Task;
        var action = model.ShowBindingDiagnosticCommand.ExecuteAsync(issue);
        Assert.False(model.BindingExplanation.Available);
        var response = await inspect(new(client.Requests.Last().Version, "1"));
        if (change == "replacement") response = response with { Properties = response.Properties.Select(property => property.Name == "Text"
            ? property with { BindingSources = BindingExplanationTestData.WithObservations(BindingNavigationTestData.Single("file:///C:/View.xaml", "replacement")) } : property).ToArray() };
        if (change == "selection") model.SelectedBindingDeclaration = model.BindingDeclarations.Single(item => item.Declaration.ExpressionId == "second-expression");
        if (change == "source") document.Content += "<!-- edited -->";
        pending.SetResult(response); await action;
        if (change == "selection") Assert.Equal("second-expression", model.SelectedBindingDeclaration!.Declaration.ExpressionId);
        else Assert.False(model.BindingExplanation.Available);
        Assert.NotEqual("replacement", model.SelectedBindingDeclaration?.BindingId);
    }
}

public sealed partial class InspectionViewModelTests
{
    [Theory]
    [InlineData("validation", false, false)]
    [InlineData("validation", false, true)]
    [InlineData("validation", true, false)]
    [InlineData("validation", true, true)]
    [InlineData("source", false, false)]
    [InlineData("source", false, true)]
    [InlineData("source", true, false)]
    [InlineData("source", true, true)]
    public async Task LiveInspectionOverlappingAnOperationRequiresAFreshObservation(string operation, bool operationStartsFirst, bool inspectionFinishesFirst)
    {
        var session = ExplanationSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session); model.SelectedNode = model.Tree[0];
        model.SelectedProperty = model.Properties.Single(property => property.Name == "Text");
        model.EditedValue = "Keep this draft";
        var issue = new WpfStudio.Runtime.Inspection.InspectionBindingIssue("text-expression", model.SelectedNode.Node.Id, "Target", "Text", "Nmae", "Demo.Model",
            "PathError", "Failure", "Active", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var inspect = session.Inspect;
        var pendingInspection = new TaskCompletionSource<InspectionElement>();
        var pendingValidation = new TaskCompletionSource<InspectionPropertyValidation>();
        InspectionNodeRequest? request = null;
        session.Inspect = value => { request = value; return pendingInspection.Task; };
        session.Validate = _ => pendingValidation.Task;
        Task validation = Task.CompletedTask;
        void StartOperation()
        {
            if (operation == "source") model.IsSourceEditRunning = true;
            else
            {
                Assert.True(model.ValidatePropertyCommand.CanExecute(null));
                validation = model.ValidatePropertyCommand.ExecuteAsync(null);
                Assert.True(model.IsPropertyOperationRunning);
            }
        }
        async Task FinishOperation()
        {
            if (operation == "source") model.IsSourceEditRunning = false;
            else { pendingValidation.SetResult(new(true)); await validation; }
        }
        if (operationStartsFirst) StartOperation();
        var selecting = model.SelectBindingIssueCommand.ExecuteAsync(issue);
        if (!operationStartsFirst) StartOperation();
        Assert.NotNull(request);
        Assert.False(model.BindingExplanation.Available);
        if (!inspectionFinishesFirst) await FinishOperation();
        var observed = await inspect(request!);
        pendingInspection.SetResult(observed with { Properties = observed.Properties.Select(property => property.Name == "Text"
            ? property with { SourceEditToken = "source-new", EditToken = "edit-new" } : property).ToArray() });
        await selecting;
        if (inspectionFinishesFirst) await FinishOperation();
        Assert.False(model.BindingExplanation.Available);
        Assert.Equal("source-new", model.SelectedProperty!.SourceEditToken);
        Assert.Equal("edit-new", model.SelectedProperty.EditToken);
        Assert.Equal("Keep this draft", model.EditedValue);

        session.Inspect = inspect;
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.BindingExplanation.Available);
        Assert.Equal("text-expression", model.SelectedBindingDeclaration!.BindingId);
        Assert.Equal("Keep this draft", model.EditedValue);
    }

    private static FakeSession ExplanationSession()
    {
        var session = BindingSourceSession();
        var inspect = session.Inspect;
        session.Inspect = async request =>
        {
            var result = await inspect(request);
            return result with { Properties = result.Properties.Select(property => property with
            { Binding = BindingExplanationTestData.Observation() with { Sources = BindingExplanationTestData.WithObservations(property.Binding!.Sources!) } }).ToArray() };
        };
        return session;
    }

    [Fact]
    public async Task LiveIssueSelectsItsExactBindingWhileKeepingAnUnrelatedEditDraft()
    {
        var session = ExplanationSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session); model.SelectedNode = model.Tree[0];
        model.SelectedProperty = model.Properties.Single(property => property.Name == "Tag"); model.EditedValue = "Tag draft";
        var issue = new WpfStudio.Runtime.Inspection.InspectionBindingIssue("text-expression", model.SelectedNode.Node.Id, "Target", "Text", "Nmae", "Demo.Model",
            "PathError", "Failure", "Active", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await model.SelectBindingIssueCommand.ExecuteAsync(issue);
        Assert.Equal("text-expression", model.SelectedBindingDeclaration!.BindingId);
        Assert.Equal("Text", model.SelectedBindingDeclaration.Property);
        Assert.True(model.BindingExplanation.Available);
        Assert.Equal("Tag", model.SelectedProperty!.Name); Assert.Equal("Tag draft", model.EditedValue);
        model.SelectedBindingDeclaration = model.BindingDeclarations.Single(item => item.Declaration.ExpressionId == "second-expression");
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("second-expression", model.SelectedBindingDeclaration!.Declaration.ExpressionId);
        Assert.Equal("Second", model.BindingExplanation.Observation!.Path);
        Assert.Equal("Tag draft", model.EditedValue);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("selection")]
    [InlineData("pause")]
    [InlineData("disconnect")]
    public async Task LiveExplanationRejectsStaleInspectionAndExpressionReplacement(string change)
    {
        var session = ExplanationSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session, debugging: true); model.SelectedNode = model.Tree[0];
        var issue = new WpfStudio.Runtime.Inspection.InspectionBindingIssue("text-expression", model.SelectedNode.Node.Id, "Target", "Text", "Nmae", "Demo.Model",
            "PathError", "Failure", "Active", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var inspect = session.Inspect;
        var pending = new TaskCompletionSource<InspectionElement>();
        InspectionNodeRequest? request = null;
        session.Inspect = value => { request = value; return pending.Task; };
        var selecting = model.SelectBindingIssueCommand.ExecuteAsync(issue);
        Assert.False(model.BindingExplanation.Available);
        var response = await inspect(request!);
        if (change == "replacement") response = response with { Properties = response.Properties.Select(property => property.Name == "Text"
            ? property with { Binding = property.Binding! with { Sources = BindingExplanationTestData.WithObservations(BindingNavigationTestData.Single("file:///C:/View.xaml", "replacement")) } } : property).ToArray() };
        if (change == "selection") model.SelectedBindingDeclaration = model.BindingDeclarations.Single(item => item.Declaration.ExpressionId == "second-expression");
        if (change == "pause") model.SetDebuggerState(true);
        if (change == "disconnect") await model.DisconnectCommand.ExecuteAsync(null);
        pending.SetResult(response); await selecting;
        if (change == "selection") Assert.Equal("second-expression", model.SelectedBindingDeclaration!.Declaration.ExpressionId);
        else Assert.False(model.BindingExplanation.Available);
        Assert.NotEqual("replacement", model.SelectedBindingDeclaration?.BindingId);
    }

    [Fact]
    public async Task LegacyAndMissingChildObservationsAreExplicitlyUnavailable()
    {
        var session = BindingSourceSession(); // Root data exists, child detail did not exist in old hosts.
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session); model.SelectedNode = model.Tree[0];
        Assert.True(model.BindingExplanation.Available);
        model.SelectedBindingDeclaration = model.BindingDeclarations.Single(item => item.Declaration.ExpressionId == "second-expression");
        Assert.False(model.BindingExplanation.Available); Assert.Contains("host", model.BindingExplanation.Status);
        var inspect = session.Inspect;
        session.Inspect = async request =>
        {
            var response = await inspect(request);
            return response with { Properties = response.Properties.Select(property => property with { Binding = property.Binding! with { Sources = null } }).ToArray() };
        };
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.False(model.BindingExplanation.Available); Assert.Contains("identities", model.BindingExplanation.Status);
    }
}
