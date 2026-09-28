using System.Collections;
using System.ComponentModel;
using System.Dynamic;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

public static partial class BindingReader
{
    public static InspectionBinding Read(BindingExpressionBase expression, BindingEvidenceCollector? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        expression.Target?.VerifyAccess();
        int remaining = 65;
        return Read(expression, evidence, ref remaining, 0);
    }

    private static InspectionBinding Read(BindingExpressionBase expression, BindingEvidenceCollector? evidence, ref int remaining, int depth)
    {
        if (remaining-- <= 0 || depth > 8)
            return new(null, expression.Status.ToString(), "Pending", "The current binding observation reached its child traversal limit; no recovery is inferred.");
        if (expression is BindingExpression binding) return ReadSingle(binding, evidence);
        var validation = BindingValidationStateReader.Capture(expression);
        var details = ReadDetails(expression, evidence, validation);
        string label = expression is MultiBindingExpression ? "MultiBinding" : expression is PriorityBindingExpression ? "PriorityBinding" : "Binding";
        bool hasValidation = validation.HasError;
        var (category, explanation) = Classify(expression.Status, hasValidation);
        if (expression is MultiBindingExpression multi)
        {
            var childrenList = new List<InspectionBinding>();
            for (int i = 0; i < multi.BindingExpressions.Count && remaining > 0; i++)
                childrenList.Add(Read(multi.BindingExpressions[i], evidence, ref remaining, depth + 1));
            var children = childrenList.ToArray();
            details = MergeChildEvidence(details, children);
            bool complete = validation.Available && multi.BindingExpressions.Count == children.Length;
            hasValidation |= children.Any(child => child.HasValidationError);
            details = details with { ValidationTruncated = details.ValidationTruncated || !complete };
            var failures = children.Where(child => child.HasValidationError ||
                child.Status is "PathError" or "UpdateSourceError" or "UpdateTargetError" || child.Category == "ChildBindingError")
                .Take(16).ToArray();
            if (failures.Length > 0)
                return new("(MultiBinding)", expression.Status.ToString(), "ChildBindingError",
                    BindingDiagnosticText.Limit("MultiBinding has failing child bindings: " + string.Join("; ", failures.Select(child =>
                        $"'{child.Path ?? "(no path)"}' is {child.Status} ({child.Category}; source: {child.SourceType ?? "unavailable"})")), 2048),
                    HasValidationError: hasValidation, Details: details);
            if (expression.Status == BindingStatus.Active && !hasValidation &&
                (!complete || children.Any(child => child.Status != "Active" || child.Category != "Active")))
                return new("(MultiBinding)", expression.Status.ToString(), "Pending",
                    "Not every MultiBinding child was verified active. A child may be pending, inactive, detached, or unattached, or the child inspection limit was reached. An active parent alone does not verify that every child recovered.",
                    HasValidationError: hasValidation, Details: details);
            if (expression.Status == BindingStatus.UpdateTargetError && !hasValidation && complete &&
                children.All(child => child.Status == "Active" && child.Category == "Active"))
                return new("(MultiBinding)", expression.Status.ToString(), category,
                    "WPF reports parent status UpdateTargetError while all inspected children report Active. A composite parent's error status can persist after later successful transfers; this status alone does not establish a current transfer failure.",
                    HasValidationError: hasValidation, Details: details);
        }
        if (expression is PriorityBindingExpression priority && priority.ActiveBindingExpression is BindingExpression active)
        {
            var result = Read(active, evidence, ref remaining, depth + 1);
            hasValidation |= result.HasValidationError;
            (category, _) = Classify(expression.Status, hasValidation);
            if (expression.Status == BindingStatus.Active && !hasValidation &&
                (!validation.Available || result.Status != "Active" || result.Category != "Active")) category = "Pending";
            // A parent's observation must not impersonate its selected child's path
            // state or identity. The source catalog exposes that exact child separately.
            return new("(PriorityBinding)", expression.Status.ToString(), category,
                BindingDiagnosticText.Limit("WPF reports parent status " + expression.Status + ". PriorityBinding currently selects child '" +
                    (result.Path ?? "(no path)") + "' with status " + result.Status + ": " + result.Explanation +
                    " The parent's raw status is reported unchanged; unused candidates do not establish failure of the selected child.", 2048),
                HasValidationError: hasValidation, Details: MergeChildEvidence(details, [result]));
        }
        if (expression.Status == BindingStatus.Active && !hasValidation &&
            (!validation.Available || expression is PriorityBindingExpression))
        {
            category = "Pending";
            explanation = expression is PriorityBindingExpression
                ? "No selected child was observed. An active parent alone does not verify a usable priority candidate."
                : "The current validation entries could not be verified; recovery is not inferred.";
        }
        return new($"({label})", expression.Status.ToString(), category,
            $"{label}: {explanation} Child bindings may have independent sources and statuses.", HasValidationError: hasValidation, Details: details);
    }

    private static InspectionBinding ReadSingle(BindingExpression expression, BindingEvidenceCollector? evidence)
    {
        object? source = expression.DataItem;
        string? path = expression.ParentBinding.Path?.Path ?? expression.ParentBinding.XPath;
        var (category, explanation) = Classify(expression.Status, expression.HasValidationError);
        var details = ReadDetails(expression, evidence, BindingValidationStateReader.Capture(expression));
        if (category == "Active" && details.ValidationTruncated)
        {
            category = "Pending";
            explanation = "WPF reports an active binding, but current validation detail coverage is incomplete; recovery is not inferred.";
        }
        var pathState = details.PathState;
        var final = pathState?.Available == true && pathState.Truncated == false ? pathState.Segments.LastOrDefault() : null;
        string? resolvedType = final?.State is "Resolved" or "Direct" or "UnsupportedAccessor" ? final.OwnerType : null;
        // ResolvedSourcePropertyName calls PropertyDescriptor.Name for descriptor
        // paths. Only use WPF's already-cached string, never that virtual getter.
        string? resolvedProperty = final?.State == "Resolved" && final.Kind == "Property" ? final.Name : null;
        if (expression.HasValidationError && details.ValidationErrors.Any(error => error.Kind == "Conversion"))
        {
            category = "Conversion";
            explanation = "WPF currently reports a conversion validation error while preparing the value to update the source. See the current validation details; recorded trace evidence is historical.";
        }
        if (expression.Status == BindingStatus.PathError && !expression.HasValidationError)
        {
            explanation = "WPF could not resolve this binding path. A missing member, unavailable source, or null intermediate value can cause this status; inspect the runtime source and binding trace.";
            if (source is null || ReferenceEquals(source, DependencyProperty.UnsetValue) || ReferenceEquals(source, BindingOperations.DisconnectedSource))
            {
                category = "SourceUnavailable";
                explanation = ReferenceEquals(source, BindingOperations.DisconnectedSource)
                    ? "WPF reports a path error while this binding's source is its disconnected-item marker. The element may have been removed or virtualized; this does not establish a misspelled member."
                    : "WPF reports a path error and no runtime source object is available. Source lookup or initialization has not produced an inspectable source; this does not establish a misspelled member.";
            }
            else if (IsMissingClrProperty(expression, source, path))
            {
                category = "MissingProperty";
                explanation = $"Runtime source '{BindingDiagnosticText.TypeName(source!.GetType())}' has no public readable CLR property '{path}'. WPF reports a path error.";
            }
            else if (pathState?.FirstUnresolvedLevel is { } failed && pathState.Segments.FirstOrDefault(segment => segment.Level == failed) is { } segment)
                explanation = $"WPF's cached path first becomes unresolved at segment {failed + 1} '{segment.Name ?? segment.Kind}'. Earlier resolved segments are shown below. WPF may erase the failed segment's owner, so a null intermediate and a missing member cannot be distinguished from these fields; no source getter was invoked.";
            else if (resolvedType is not null && resolvedProperty is not null)
                explanation += $" WPF currently caches resolved owner '{resolvedType}' and property '{resolvedProperty}'; no source getter was invoked to investigate further.";
        }
        return new(path is null ? null : BindingDiagnosticText.Limit(path, 2048), expression.Status.ToString(), category, BindingDiagnosticText.Limit(explanation, 2048),
            AvailableType(source), resolvedType, resolvedProperty,
            expression.HasValidationError, details);
    }

    private static bool IsMissingClrProperty(BindingExpression expression, object? source, string? path)
    {
        if (source is null or DependencyObject or ICustomTypeDescriptor or ICustomTypeProvider or IDynamicMetaObjectProvider or IEnumerable or IListSource ||
            !string.IsNullOrEmpty(expression.ParentBinding.XPath) || string.IsNullOrEmpty(path) || path.Length > 2048 ||
            !(path[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_') ||
            path.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            return false;
        Type type = source.GetType();
        if (type.IsCOMObject || type.GetInterfaces().Any(i => i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))))
            return false;
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.CustomAttributes.Any(a => a.AttributeType == typeof(TypeDescriptionProviderAttribute))) return false;
        // No source getter or nested path segment is evaluated. The explanation
        // describes the observed CLR surface, not a guess about null intermediates.
        return !type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(property => property.Name == path &&
            property.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0);
    }

    private static string? AvailableType(object? value) => value is null
        || ReferenceEquals(value, DependencyProperty.UnsetValue) || ReferenceEquals(value, BindingOperations.DisconnectedSource)
        ? null : BindingDiagnosticText.TypeName(value.GetType());

    private static (string Category, string Explanation) Classify(BindingStatus status, bool validation) => validation
        ? ("Validation", "WPF reports a validation error for this binding.")
        : status switch
        {
            BindingStatus.Active => ("Active", "WPF reports an active binding."),
            BindingStatus.PathError => ("PathError", "WPF could not resolve the binding path."),
            BindingStatus.UpdateTargetError => ("UpdateTargetError", "WPF could not transfer the source value to the target. Conversion, validation, or target-type constraints may be involved."),
            BindingStatus.UpdateSourceError => ("UpdateSourceError", "WPF could not transfer the target value to the source. Conversion, validation, or source update failures may be involved."),
            BindingStatus.AsyncRequestPending => ("Pending", "The asynchronous binding request is pending."),
            BindingStatus.Detached => ("Detached", "The binding has detached from its target."),
            BindingStatus.Inactive => ("Inactive", "The binding is inactive, which may be expected for unused PriorityBinding candidates."),
            _ => ("Unattached", "The binding has not attached to a source.")
        };
}
