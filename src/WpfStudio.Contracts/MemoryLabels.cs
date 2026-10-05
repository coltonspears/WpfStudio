namespace WpfStudio.Contracts.Profiling;

/// <summary>Plain-language names shared by the worker and the UI, so the Sankey, graph and inspector agree.</summary>
public static class MemoryLabels
{
    public const string StaticPrefix = "Static ";

    /// <summary>Lower is longer-lived and more interesting when explaining why an object is alive.</summary>
    public static int RootPriority(string kind, string label) =>
        label.StartsWith(StaticPrefix, StringComparison.Ordinal) ? 0 : kind switch
        {
            "StaticVar" or "ThreadStaticVar" => 0,
            "StrongHandle" or "SizedRefHandle" or "RefCountedHandle" => 1,
            "PinnedHandle" or "AsyncPinnedHandle" => 2,
            "Stack" => 3,
            "Frozen segment" => 4,
            "FinalizerQueue" => 5,
            _ => 3
        };

    public static string RootKindName(string kind) => kind switch
    {
        "StrongHandle" => "Strong handle",
        "PinnedHandle" => "Pinned handle",
        "AsyncPinnedHandle" => "Async pinned handle",
        "RefCountedHandle" => "Ref-counted handle",
        "SizedRefHandle" => "Sized-ref handle",
        "Stack" => "Stack",
        "FinalizerQueue" => "Finalizer queue",
        "Frozen segment" => "Frozen segment",
        "StaticVar" => "Static variable",
        "ThreadStaticVar" => "Thread static",
        _ => kind
    };

    /// <summary>A short label for one GC root, e.g. "Static Cache.Pages", "Strong handle" or "Stack: Program.Main".</summary>
    public static string RootDisplay(string kind, string label)
    {
        if (label.StartsWith(StaticPrefix, StringComparison.Ordinal)) return label;
        if (kind == "Stack" && label.StartsWith("Stack: ", StringComparison.Ordinal))
        {
            var text = label;
            var slot = text.LastIndexOf(" (slot ", StringComparison.Ordinal);
            if (slot > 0) text = text[..slot];
            var paren = text.IndexOf('(');
            if (paren > 0) text = text[..paren];
            return text.Length > 72 ? text[..72] + "…" : text;
        }
        return RootKindName(kind);
    }

    /// <summary>"Namespace.Type.Field" becomes "Type.Field"; a "Static " prefix is dropped.</summary>
    public static string ShortStatic(string qualified)
    {
        if (qualified.StartsWith(StaticPrefix, StringComparison.Ordinal)) qualified = qualified[StaticPrefix.Length..];
        var generic = qualified.IndexOf('<');
        var lastDot = qualified.LastIndexOf('.');
        if (lastDot <= 0) return qualified;
        var typeDot = qualified.LastIndexOf('.', lastDot - 1);
        if (generic >= 0 && generic < lastDot)
        {
            // Generic declaring type: shorten its name but keep the field.
            var field = qualified[(lastDot + 1)..];
            return ShortType(qualified[..lastDot]) + "." + field;
        }
        return typeDot < 0 ? qualified : qualified[(typeDot + 1)..].Replace('+', '.');
    }

    /// <summary>The type name without its namespace, keeping generic arguments short as well.</summary>
    public static string ShortType(string name)
    {
        if (string.IsNullOrEmpty(name)) return "<unknown type>";
        var generic = name.IndexOf('<');
        var head = generic < 0 ? name : name[..generic];
        var dot = head.LastIndexOf('.');
        var plus = head.LastIndexOf('+');
        var cut = Math.Max(dot, plus);
        var shortHead = cut < 0 ? head : head[(cut + 1)..];
        if (generic < 0) return shortHead;
        var close = name.LastIndexOf('>');
        var args = close > generic ? name[(generic + 1)..close] : name[(generic + 1)..];
        return shortHead + "<" + string.Join(", ", SplitGenericArguments(args).Select(ShortType)) + ">" + ArraySuffix(name);
    }

    public static string Namespace(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var generic = name.IndexOf('<');
        var head = generic < 0 ? name : name[..generic];
        var bracket = head.IndexOf('[');
        if (bracket > 0) head = head[..bracket];
        var plus = head.IndexOf('+');
        if (plus > 0) head = head[..plus];
        var dot = head.LastIndexOf('.');
        return dot < 0 ? "<global>" : head[..dot];
    }

    public static string GenerationName(string generation) => generation switch
    {
        "Generation0" => "Gen 0", "Generation1" => "Gen 1", "Generation2" => "Gen 2", "Large" => "Large object heap",
        "Pinned" => "Pinned object heap", "Frozen" => "Frozen", _ => generation
    };

    /// <summary>True for runtime, BCL and WPF assemblies. Leak inspections focus on application types.</summary>
    public static bool IsFrameworkModule(string module)
    {
        var name = module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? module[..^4] : module;
        return name.StartsWith("System", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
            name is "mscorlib" or "netstandard" or "PresentationCore" or "PresentationFramework" or "WindowsBase" or
                "DirectWriteForwarder" or "UIAutomationTypes" or "UIAutomationProvider" or "ReachFramework" or "WindowsFormsIntegration" ||
            name.StartsWith("PresentationFramework.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("PresentationUI", StringComparison.OrdinalIgnoreCase);
    }

    private static string ArraySuffix(string name)
    {
        var close = name.LastIndexOf('>');
        return close >= 0 && close < name.Length - 1 ? name[(close + 1)..] : "";
    }

    private static IEnumerable<string> SplitGenericArguments(string args)
    {
        var depth = 0; var start = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == '<') depth++;
            else if (args[i] == '>') depth--;
            else if (args[i] == ',' && depth == 0) { yield return args[start..i].Trim(); start = i + 1; }
        }
        if (start < args.Length) yield return args[start..].Trim();
    }
}
