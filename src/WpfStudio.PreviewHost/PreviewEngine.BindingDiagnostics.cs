using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.PreviewHost;

public sealed partial class PreviewEngine
{
    // Created before any preview XAML or project view is loaded. The collector
    // retains bounded copied facts and weak identities, never a model graph.
    private readonly BindingEvidenceCollector _bindingEvidence = new(TemporaryPropertyEdits.IsOverrideBinding);

    private static bool IsBindingFailure(InspectionBinding binding) => binding.HasValidationError ||
        binding.Status is "PathError" or "UpdateSourceError" or "UpdateTargetError" || binding.Category == "ChildBindingError";

    private static PreviewDiagnostic BindingDiagnostic(string nodeId, string property, InspectionBinding binding, string? bindingId) =>
        new($"Binding '{binding.Path ?? "(unspecified path)"}' is {binding.Status}. {binding.Explanation}", "Error",
            NodeId: nodeId, Property: property, BindingId: bindingId);
}
