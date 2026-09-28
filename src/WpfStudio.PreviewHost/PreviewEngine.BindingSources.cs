using System.Windows.Data;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.PreviewHost;

public sealed partial class PreviewEngine
{
    private BindingSourceCatalog _bindingSources = new();

    public Task<BindingSourceResponse> GetBindingSourceAsync(BindingSourceRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() =>
        {
            try
            {
                if (request.Revision != _version || !_objects.TryGetValue(request.NodeId, out var target) || !IsInCurrentPreview(target))
                    return Reject("The preview or selected element has changed. Refresh the selection.");
                if (request.PropertyId is not null)
                    return Reject("A live-inspection property token cannot identify a preview property.");
                var property = FindProperty(target, new PreviewPropertyEdit(request.Revision, request.NodeId,
                    request.Property, null, OwnerType: request.OwnerType, OwnerAssembly: request.OwnerAssembly));
                var expression = BindingOperations.GetBindingExpressionBase(target, property);
                if (expression is null || TemporaryPropertyEdits.IsOverrideBinding(expression) || _scenarioActivation?.OwnsDataContext(target, property) == true)
                    return Reject("The selected property no longer has the observed application binding.");
                var response = _bindingSources.Validate(expression, request);
                if (request.Revision != _version || !_objects.TryGetValue(request.NodeId, out var currentTarget) ||
                    !ReferenceEquals(currentTarget, target) || !ReferenceEquals(expression.Target, target) ||
                    expression.TargetProperty != property || expression.Status is BindingStatus.Detached or BindingStatus.Unattached ||
                    !ReferenceEquals(BindingOperations.GetBindingExpressionBase(target, property), expression) || !IsInCurrentPreview(target))
                    return Reject("The target or binding changed while its declaration was inspected.");
                if (response.Declaration is { } declaration && _document is not null)
                {
                    var mapped = _document.MapBindingSource(declaration);
                    response = response with { Declaration = mapped, Available = response.Available && mapped.Source is not null,
                        Status = mapped.UnavailableReason ?? response.Status };
                }
                return Task.FromResult(response);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            { return Reject(exception.GetBaseException().Message); }

            Task<BindingSourceResponse> Reject(string message) => Task.FromResult(new BindingSourceResponse(request, false, Status: message));
        }, cancellationToken);

    private BindingSourcesSnapshot? CaptureBindingSources(BindingExpressionBase? expression, ref int rows, ref int characters,
        BindingDetailBudget budget)
    {
        if (expression is null) return null;
        if (rows <= 0 || characters < 512)
        {
            var omitted = characters >= 32 ? _bindingSources.Capture(expression, maximumDeclarations: 0, maximumCharacters: 0)
                : new BindingSourcesSnapshot("", [], Truncated: true);
            characters -= BindingSourceCatalog.GetCharacterCount(omitted);
            return omitted;
        }
        var captured = _bindingSources.Capture(expression, maximumDeclarations: Math.Min(rows, 65),
            maximumCharacters: Math.Min(characters, 16384), observe: value => budget.Take(BindingReader.Read(value, _bindingEvidence)));
        if (_document is not null)
            captured = captured with { Declarations = captured.Declarations.Select(_document.MapBindingSource).ToArray() };
        while (captured.Declarations.Count > 0 && BindingSourceCatalog.GetCharacterCount(captured) > characters)
            captured = captured with { Declarations = captured.Declarations.Take(captured.Declarations.Count - 1).ToArray(), Truncated = true };
        rows -= captured.Declarations.Count;
        characters -= BindingSourceCatalog.GetCharacterCount(captured);
        return captured;
    }
}
