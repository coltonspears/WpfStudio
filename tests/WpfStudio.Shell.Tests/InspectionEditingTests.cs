using WpfStudio.App.Features.Inspection;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

public sealed partial class InspectionViewModelTests
{
    private static InspectionProperty Editable(string value = "original", string token = "v1", bool overridden = false) =>
        new("Text", "TextBlock", "PresentationFramework", "System.String", "display abbreviated", "Local", true, false, false,
            PropertyId: "opaque-dp", EditToken: token, CanEdit: true, EditableValue: value, IsOverridden: overridden);

    private static FakeSession EditingSession(Func<InspectionProperty> property) => new()
    { Inspect = request => Task.FromResult(new InspectionElement(request.Revision, request.NodeId, [property()], null, true)) };

    private static void SelectProperty(InspectionViewModel model)
    { model.SelectedNode = model.Tree[0]; model.SelectedProperty = Assert.Single(model.Properties); }

    [Fact]
    public async Task ValidationAndApplyUseExactIdentityAndFullValueWithExplicitNull()
    {
        var property = Editable(new string('a', 6000));
        var session = EditingSession(() => property);
        var validations = new List<InspectionPropertyEdit>(); var edits = new List<InspectionPropertyEdit>();
        session.Validate = r => { validations.Add(r); return Task.FromResult(new InspectionPropertyValidation(true)); };
        session.Edit = r => { edits.Add(r); return Task.FromResult(new InspectionPropertyEditResult(r.OperationId, "Rejected", Error: "test")); };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session); SelectProperty(model);
        Assert.Equal(property.EditableValue, model.EditedValue);
        await model.ValidatePropertyCommand.ExecuteAsync(null);
        Assert.Empty(edits);
        var validation = Assert.Single(validations);
        Assert.Equal("opaque-dp", validation.PropertyId); Assert.Equal("v1", validation.EditToken);
        Assert.Equal("first", validation.NodeId); Assert.Equal(6000, validation.Value!.Length);
        model.EditedValue = "";
        await model.ApplyPropertyCommand.ExecuteAsync(null);
        model.EditAsNull = true;
        await model.ApplyPropertyCommand.ExecuteAsync(null);
        Assert.Equal("", edits[0].Value); Assert.False(edits[0].IsNull); Assert.True(edits[1].IsNull);
        Assert.NotEqual(edits[0].OperationId, edits[1].OperationId);
    }

    [Fact]
    public async Task DirtyDraftSurvivesGridSelectionResetAndRequiresReloadAfterExternalChange()
    {
        var property = Editable(); var session = EditingSession(() => property);
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session); SelectProperty(model); model.EditedValue = "draft";
        // WPF DataGrid resets SelectedItem when its items collection is cleared.
        model.Properties.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) model.SelectedProperty = null; };
        property = Editable("app changed", "v2");
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("draft", model.EditedValue); Assert.True(model.IsEditStale);
        Assert.False(model.ApplyPropertyCommand.CanExecute(null));
        model.ReloadPropertyValueCommand.Execute(null);
        Assert.Equal("app changed", model.EditedValue); Assert.False(model.IsEditStale);
        Assert.True(model.ApplyPropertyCommand.CanExecute(null));
    }

    [Fact]
    public async Task TreeContainerRebuildKeepsPropertySelectionAndDirtyDraft()
    {
        var session = EditingSession(() => Editable());
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session); SelectProperty(model); model.EditedValue = "draft";
        model.Tree.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) model.SelectedNode = null;
        };
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("first", model.SelectedNode?.Node.Id);
        Assert.Equal("opaque-dp", model.SelectedProperty?.PropertyId);
        Assert.Equal("draft", model.EditedValue);
        Assert.True(model.ApplyPropertyCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnknownOutcomePreventsReplayAndChecksTheSameOperation()
    {
        var property = Editable(); var session = EditingSession(() => property); int writes = 0; string? operation = null;
        session.Edit = r => { writes++; operation = r.OperationId; return Task.FromResult(new InspectionPropertyEditResult(r.OperationId, "Unknown")); };
        session.EditStatus = r =>
        {
            Assert.Equal(operation, r.OperationId); property = Editable("temporary", "v2", true);
            return Task.FromResult(new InspectionPropertyEditResult(r.OperationId, "Applied"));
        };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session); SelectProperty(model); model.EditedValue = "temporary";
        await model.ApplyPropertyCommand.ExecuteAsync(null);
        Assert.False(model.ApplyPropertyCommand.CanExecute(null)); Assert.True(model.CheckEditOutcomeCommand.CanExecute(null));
        await model.ApplyPropertyCommand.ExecuteAsync(null); Assert.Equal(1, writes);
        await model.CheckEditOutcomeCommand.ExecuteAsync(null);
        Assert.Equal(1, writes); Assert.True(model.SelectedProperty!.IsOverridden);
        Assert.False(model.CheckEditOutcomeCommand.CanExecute(null)); Assert.True(model.ResetPropertyCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewerTypedDraftSurvivesApplyAndUsesConfirmedPostEditToken()
    {
        var property = Editable(); var session = EditingSession(() => property);
        var pending = new TaskCompletionSource<InspectionPropertyEditResult>(); InspectionPropertyEdit? sent = null;
        session.Edit = r => { sent = r; return pending.Task; };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session); SelectProperty(model); model.EditedValue = "first edit";
        var applying = model.ApplyPropertyCommand.ExecuteAsync(null);
        model.EditedValue = "next draft";
        property = Editable("first edit", "v2", true);
        pending.SetResult(new(sent!.OperationId, "Applied", new(sent.Revision, sent.NodeId, [property], null, true)));
        await applying;
        Assert.Equal("next draft", model.EditedValue); Assert.False(model.IsEditStale);
        Assert.True(model.ApplyPropertyCommand.CanExecute(null));
    }

    [Fact]
    public async Task LateEditCannotOverwriteReplacementSessionOrItsBusyState()
    {
        var first = EditingSession(() => Editable()); var second = EditingSession(() => Editable("second"));
        var oldReply = new TaskCompletionSource<InspectionPropertyEditResult>(); var newReply = new TaskCompletionSource<InspectionPropertyValidation>();
        first.Edit = r => oldReply.Task; second.Validate = r => newReply.Task;
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(first); SelectProperty(model);
        var oldEdit = model.ApplyPropertyCommand.ExecuteAsync(null);
        await model.AttachAsync(second); SelectProperty(model);
        var validating = model.ValidatePropertyCommand.ExecuteAsync(null);
        Assert.True(model.IsPropertyOperationRunning);
        oldReply.SetResult(new("old", "Unknown", Error: "old session")); await oldEdit;
        Assert.True(model.IsPropertyOperationRunning); Assert.DoesNotContain("old session", model.EditStatus);
        newReply.SetResult(new(true)); await validating;
        Assert.False(model.IsPropertyOperationRunning); Assert.Equal("second", model.EditedValue);
    }

    [Fact]
    public async Task ResetUsesCurrentObservedTokenAndPauseDisablesMutations()
    {
        var property = Editable("temporary", "v2", true); var session = EditingSession(() => property);
        InspectionPropertyEdit? sent = null; session.Edit = r => { sent = r; return Task.FromResult(new InspectionPropertyEditResult(r.OperationId, "Reset")); };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session, debugging: true); SelectProperty(model); model.EditedValue = "draft";
        property = Editable("newer temporary", "v3", true); await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.IsEditStale); Assert.True(model.ResetPropertyCommand.CanExecute(null));
        model.SetDebuggerState(true);
        Assert.False(model.ApplyPropertyCommand.CanExecute(null)); Assert.False(model.ResetPropertyCommand.CanExecute(null));
        await model.ResetPropertyCommand.ExecuteAsync(null); Assert.Null(sent);
        model.SetDebuggerState(false); await model.ResetPropertyCommand.ExecuteAsync(null);
        Assert.True(sent!.Reset); Assert.Equal("v3", sent.EditToken); Assert.Null(sent.Value);
    }
}
