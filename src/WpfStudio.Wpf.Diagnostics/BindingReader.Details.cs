using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

public static partial class BindingReader
{
    private static InspectionBindingDetails ReadDetails(BindingExpressionBase expression, BindingEvidenceCollector? collector,
        BindingValidationStateReader.Result validation)
    {
        var errors = validation.Errors.Select(error =>
        {
            Type? rule = error.RuleInError?.GetType();
            string kind = rule?.Assembly == typeof(Binding).Assembly ? rule.FullName switch
            {
                "System.Windows.Controls.ConversionValidationRule" => "Conversion",
                "System.Windows.Controls.ExceptionValidationRule" => "SourceUpdateException",
                "System.Windows.Controls.DataErrorValidationRule" => "DataError",
                "System.Windows.Controls.NotifyDataErrorValidationRule" => "NotifyDataError",
                _ => "Validation"
            } : "Validation";
            // ErrorContent is user supplied. Never call an arbitrary ToString or an
            // exception's virtual Message getter while observing a running app.
            return new InspectionBindingValidation(kind, rule is null ? "(unknown rule)" : BindingDiagnosticText.TypeName(rule),
                error.RuleInError?.ValidationStep.ToString() ?? "Unknown", SafeValidationMessage(error.ErrorContent),
                error.Exception is null ? null : BindingDiagnosticText.TypeName(error.Exception.GetType()));
        }).ToArray();
        var history = collector?.Read(expression) ?? (Events: (IReadOnlyList<InspectionBindingEvidence>)[], Truncated: false);
        if (expression is BindingExpression single)
        {
            var binding = single.ParentBinding;
            string kind;
            string description;
            if (binding.ElementName is { Length: > 0 } name)
            {
                kind = "ElementName";
                description = "Named-element source: " + BindingDiagnosticText.Limit(name, 256) + ".";
            }
            else if (binding.RelativeSource is { } relative)
            {
                kind = "RelativeSource";
                description = "RelativeSource " + relative.Mode + (relative.Mode == RelativeSourceMode.FindAncestor
                    ? $" ({(relative.AncestorType is null ? "unspecified type" : BindingDiagnosticText.TypeName(relative.AncestorType))}, level {relative.AncestorLevel.ToString(CultureInfo.InvariantCulture)})" : "") + ".";
            }
            else if (binding.Source is not null)
            {
                kind = "ExplicitSource";
                description = "An explicit Binding.Source is configured.";
            }
            else
            {
                kind = "DataContextOrNullSource";
                description = "WPF exposes no named, relative, or non-null explicit source. This normally uses DataContext; the public API cannot distinguish an explicitly null Source.";
            }
            description += " SourceType and resolved owner describe WPF's current cached objects; no source property was evaluated. Recorded evidence is historical, even when the root source identity still matches.";
            return new(kind, binding.Mode.ToString(), binding.Converter is null ? null : BindingDiagnosticText.TypeName(binding.Converter.GetType()),
                description + (validation.Available ? "" : " Current validation details could not be read with this WPF implementation."),
                errors, history.Events, history.Truncated, validation.Truncated,
                BindingPathStateReader.Capture(single));
        }
        return new("Composite", expression.ParentBindingBase is MultiBinding multi ? multi.Mode.ToString() : "ChildDefined",
            expression.ParentBindingBase is MultiBinding { Converter: { } converter } ? BindingDiagnosticText.TypeName(converter.GetType()) : null,
            "Child bindings have independent sources. Validation rows here belong only to this expression; select a child for its own current validation. Recorded evidence is historical and does not establish the current cause."
                + (validation.Available ? "" : " This expression's current validation entries could not be read with this WPF implementation."),
            errors, history.Events, history.Truncated, validation.Truncated);
    }

    private static string? SafeValidationMessage(object? value) => value switch
    {
        null => null,
        string text => BindingDiagnosticText.Limit(text, 512),
        bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid =>
            BindingDiagnosticText.Limit(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", 512),
        _ => "(" + BindingDiagnosticText.TypeName(value.GetType()) + "; content was not evaluated)"
    };

    private static InspectionBindingDetails MergeChildEvidence(InspectionBindingDetails details, InspectionBinding[] children)
    {
        var events = details.Evidence.Concat(children.SelectMany(child => child.Details?.Evidence ?? []))
            .DistinctBy(item => item.Id).OrderByDescending(item => item.Id).Take(3).ToArray();
        return details with
        {
            Evidence = events,
            EvidenceTruncated = details.EvidenceTruncated || children.Any(child => child.Details?.EvidenceTruncated == true) ||
                details.Evidence.Count + children.Sum(child => child.Details?.Evidence.Count ?? 0) > events.Length,
            ValidationTruncated = details.ValidationTruncated || children.Any(child => child.Details?.ValidationTruncated != false)
        };
    }
}

/// <summary>Limits added detail text across a response, without hiding current binding status.</summary>
public sealed class BindingDetailBudget(int maximumCharacters)
{
    private int _remaining = Math.Max(0, maximumCharacters);

    public static int GetCharacterCount(InspectionBinding binding)
    {
        long count = 0L + (binding.Path?.Length ?? 0) + binding.Status.Length + binding.Category.Length + binding.Explanation.Length +
            (binding.SourceType?.Length ?? 0) + (binding.ResolvedSourceType?.Length ?? 0) + (binding.ResolvedProperty?.Length ?? 0) + 128;
        if (binding.Details is { } details) count += DetailCount(details);
        return (int)Math.Min(int.MaxValue, count);
    }

    private static long DetailCount(InspectionBindingDetails details)
    {
        long count = 0L + details.SourceKind.Length + details.Mode.Length + (details.ConverterType?.Length ?? 0) + details.SourceDescription.Length;
        foreach (var error in details.ValidationErrors)
            count += error.Kind.Length + error.RuleType.Length + error.ValidationStep.Length + (error.Message?.Length ?? 0) + (error.ExceptionType?.Length ?? 0) + 64;
        foreach (var item in details.Evidence)
            count += item.Kind.Length + item.Message.Length + (item.Member?.Length ?? 0) + (item.OwnerType?.Length ?? 0)
                + (item.SourceType?.Length ?? 0) + item.ObservedStatus.Length + (item.BindingPath?.Length ?? 0) + 128;
        if (details.PathState is { } path)
        {
            count += path.Status.Length + (path.UnavailableReason?.Length ?? 0) + (path.TargetType?.Length ?? 0) + 64;
            foreach (var segment in path.Segments)
                count += segment.Kind.Length + (segment.Name?.Length ?? 0) + segment.State.Length + (segment.OwnerType?.Length ?? 0)
                    + (segment.AccessorKind?.Length ?? 0) + (segment.ValueType?.Length ?? 0) + 64;
        }
        return count;
    }

    public InspectionBinding Take(InspectionBinding binding)
    {
        if (binding.Details is not { } details) return binding;
        long length = DetailCount(details);
        if (length <= _remaining) { _remaining -= (int)length; return binding; }
        // The current status, category, validation flag, and source metadata remain
        // available. Omit detail payloads once the aggregate budget is exhausted.
        return binding with { Details = new("Omitted", "Unknown", null,
            "Additional details omitted because this response reached its diagnostic text budget.", [], [], true, true) };
    }
}
