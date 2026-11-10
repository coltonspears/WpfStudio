using System.IO;
using System.Reflection;

namespace WpfStudio.PreviewHost;

/// <summary>Finds the view model a view is conventionally paired with, by name: OrdersView or OrdersPage with OrdersViewModel, MainWindow with MainWindowViewModel or MainViewModel.</summary>
internal static class DesignDataConventions
{
    private static readonly string[] ViewSuffixes = ["UserControl", "Window", "Dialog", "Control", "Screen", "Page", "View", "Pane", "Panel", "Form"];
    private const int MaximumReferences = 32;

    /// <returns>The single best match, or the names of the candidates when several match equally well.</returns>
    public static (Type? ViewModel, string? Ambiguity) FindViewModel(string viewClassName, Assembly project)
    {
        int dot = viewClassName.LastIndexOf('.');
        string simple = viewClassName.Substring(dot + 1), viewNamespace = dot < 0 ? "" : viewClassName.Substring(0, dot);
        if (simple.Length == 0) return (null, null);
        string stem = ViewSuffixes.Where(suffix => simple.Length > suffix.Length && simple.EndsWith(suffix, StringComparison.Ordinal))
            .Select(suffix => simple.Substring(0, simple.Length - suffix.Length)).FirstOrDefault() ?? simple;
        // In order of preference: the full view name before its stem, and the usual suffix before the abbreviation.
        string[] names = new[] { simple + "ViewModel", stem + "ViewModel", simple + "VM", stem + "VM" }.Distinct(StringComparer.Ordinal).ToArray();

        var candidates = Candidates(project, names);
        // View models often live in a core library the view's project references.
        if (candidates.Count == 0)
            foreach (var reference in References(project))
                candidates.AddRange(Candidates(reference, names));
        if (candidates.Count == 0) return (null, null);
        var ranked = candidates.OrderBy(type => Array.IndexOf(names, type.Name)).ThenByDescending(type => SharedSegments(type.Namespace ?? "", viewNamespace))
            .ThenBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        bool tie = ranked.Length > 1 && ranked[1].Name == ranked[0].Name
            && SharedSegments(ranked[1].Namespace ?? "", viewNamespace) == SharedSegments(ranked[0].Namespace ?? "", viewNamespace);
        return tie ? (null, string.Join(", ", ranked.Where(type => type.Name == ranked[0].Name).Take(4).Select(type => type.FullName))) : (ranked[0], null);
    }

    private static List<Type> Candidates(Assembly assembly, string[] names) => DesignSampleFactory.LoadableTypes(assembly)
        .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters && (type.IsPublic || type.IsNotPublic) && Array.IndexOf(names, type.Name) >= 0).ToList();

    private static IEnumerable<Assembly> References(Assembly project)
    {
        int loaded = 0;
        foreach (var name in project.GetReferencedAssemblies())
        {
            if (loaded >= MaximumReferences) yield break;
            Assembly? reference;
            try { reference = Assembly.Load(name); }
            // A reference the build did not copy is the view's problem to report if it needs it, not this search's.
            catch (Exception exception) when (exception is IOException or BadImageFormatException) { continue; }
            if (DesignSampleFactory.IsFramework(reference)) continue;
            loaded++;
            yield return reference;
        }
    }

    private static int SharedSegments(string first, string second)
    {
        string[] left = first.Split('.'), right = second.Split('.');
        int shared = 0;
        while (shared < left.Length && shared < right.Length && left[shared] == right[shared]) shared++;
        return shared;
    }
}
