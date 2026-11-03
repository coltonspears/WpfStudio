using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.BindingSources;

/// <summary>
/// What the declarations panel asks of the view model observing an element. The designer and live inspection verify a
/// declaration differently (against the preview's source or the running session's modules), so that stays with them.
/// </summary>
public interface IBindingDeclarationOwner
{
    bool CanShowBindingSource(BindingDeclarationItem? item);
    Task ShowBindingSourceAsync(BindingDeclarationItem? item);
    /// <summary>The list, the selection or a verification changed: update the status and what depends on the selection.</summary>
    void BindingDeclarationsChanged();
}

/// <summary>Why <see cref="BindingDeclarationsViewModel.Status"/> says what it says, when that is not just the latest outcome.</summary>
public enum BindingSourceCondition { None, Verifying, DebuggerPaused }

/// <summary>The binding declarations observed on one element, the one selected, and what is known about opening its source.</summary>
public sealed partial class BindingDeclarationsViewModel(IBindingDeclarationOwner owner) : ObservableObject
{
    private bool _updating, _selectionRemoved;
    public ObservableCollection<BindingDeclarationItem> Items { get; } = [];
    [ObservableProperty] public partial BindingDeclarationItem? Selected { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Select an element to inspect its binding declarations.";
    [ObservableProperty] public partial string Coverage { get; set; } = "";
    /// <summary>A verification or navigation is running. Its guard ends with it, so a late answer cannot apply.</summary>
    internal bool IsOpening { get; private set; }
    internal BindingSourceCondition Condition { get; private set; }
    /// <summary>Changes when a running verification no longer applies.</summary>
    internal long Epoch { get; private set; }
    /// <summary>Changes when the user chooses a declaration, property or element, so an automatic choice can yield to it.</summary>
    internal long UserSelection { get; private set; }

    partial void OnStatusChanged(string value) => Condition = BindingSourceCondition.None;
    partial void OnSelectedChanged(BindingDeclarationItem? value)
    {
        if (_updating) return;
        ++UserSelection;
        if (value is not null) _selectionRemoved = false;
        SelectionChanged();
    }

    private bool CanShowSource(BindingDeclarationItem? item) => owner.CanShowBindingSource(item);
    [RelayCommand(CanExecute = nameof(CanShowSource))]
    private Task ShowSourceAsync(BindingDeclarationItem? item) => owner.ShowBindingSourceAsync(item);

    internal void AdvanceEpoch() => ++Epoch;
    internal void NoteUserSelection() => ++UserSelection;
    internal void ForgetRemoval() => _selectionRemoved = false;
    internal void MarkSelectionRemoved() => _selectionRemoved = true;
    internal void ShowCondition(BindingSourceCondition condition, string status) { Status = status; Condition = condition; }

    /// <summary>
    /// Shows a new observation's declarations, keeping the selected expression if it survived. One that did not is
    /// remembered as removed instead of moving the selection: another expression is not the same binding.
    /// </summary>
    internal void Replace(IEnumerable<BindingDeclarationItem> observed, BindingDeclarationItem? previous, bool truncated, Func<BindingDeclarationItem, bool> isSelectedProperty)
    {
        Coverage = truncated ? "Declaration inspection reached its limit; some bindings or composite children were omitted. Listed declarations can still be verified." : "";
        _updating = true;
        try
        {
            Items.Clear();
            foreach (var item in observed) Items.Add(item);
            var retained = Items.FirstOrDefault(item => item.SameIdentity(previous));
            if (previous is not null && retained is null) _selectionRemoved = true;
            Selected = retained ?? (_selectionRemoved ? null : Items.FirstOrDefault(isSelectedProperty) ?? Items.FirstOrDefault());
        }
        finally { _updating = false; }
        if (!(Selected?.SameIdentity(previous) ?? previous is null)) SelectionChanged();
        owner.BindingDeclarationsChanged();
    }

    /// <summary>The selected property's binding becomes the selection when it has one.</summary>
    internal void PropertySelected(string? bindingId)
    {
        ++UserSelection;
        ++Epoch;
        if (bindingId is not null)
            Selected = Items.FirstOrDefault(item => item.BindingId == bindingId && item.Declaration.ParentExpressionId is null);
        owner.BindingDeclarationsChanged();
    }

    /// <summary>Selects a declaration on the program's behalf: unlike the user's choice, it says nothing about what they want next.</summary>
    internal void SelectObserved(BindingDeclarationItem? item)
    {
        _updating = true;
        try { Selected = item; }
        finally { _updating = false; }
        SelectionChanged();
    }

    /// <summary>
    /// Verifies a declaration and opens its source, reporting what came of it. <paramref name="current"/>,
    /// <paramref name="verify"/> and <paramref name="open"/> are the owner's, whose guards and sources differ.
    /// A null from <paramref name="open"/> means its context changed before it could open anything.
    /// </summary>
    internal async Task OpenAsync(BindingDeclarationItem item, Func<bool> current, Func<CancellationToken, Task<BindingSourceResponse>> verify,
        Func<BindingSourceDeclaration, CancellationToken, Task<string?>> open, CancellationToken token)
    {
        IsOpening = true;
        ShowCondition(BindingSourceCondition.Verifying, "Verifying binding declaration source…");
        owner.BindingDeclarationsChanged();
        try
        {
            var response = await verify(token);
            if (!response.Available || response.Declaration?.Source is null)
            { Status = response.Status ?? response.Declaration?.UnavailableReason ?? item.Description; return; }
            if (await open(response.Declaration, token) is { } status && current()) Status = status;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (current()) Status = "Binding source unavailable: " + exception.Message; }
        finally
        {
            if (!current() && Condition == BindingSourceCondition.Verifying) Status = "Binding source navigation cancelled because its context changed.";
            IsOpening = false;
            owner.BindingDeclarationsChanged();
        }
    }

    private void SelectionChanged()
    {
        ++Epoch;
        Status = Selected?.Description ?? "No binding declaration was observed for this element.";
        owner.BindingDeclarationsChanged();
    }
}
