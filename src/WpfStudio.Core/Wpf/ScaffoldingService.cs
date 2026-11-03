using System.Text.RegularExpressions;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core.Wpf;

public enum ScaffoldKind { ViewAndViewModel, UserControl, ResourceDictionary, Converter, ViewModel, ObservableProperty, RelayCommand }

public sealed partial class ScaffoldingService
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();
    private static readonly HashSet<string> Keywords = new("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while".Split(' '), StringComparer.Ordinal);
    public async Task<IReadOnlyList<FileChange>> CreateAsync(string projectPath, string directory, string name, ScaffoldKind kind, bool addDataTemplate = false, CancellationToken token = default)
    {
        if (!Identifier().IsMatch(name) || Keywords.Contains(name)) throw new ArgumentException("Enter a valid C# identifier that is not a reserved keyword.");
        projectPath = Path.GetFullPath(projectPath);
        var project = new ProjectFileEditor(projectPath, await File.ReadAllTextAsync(projectPath, token));
        var rootDirectory = Path.GetDirectoryName(projectPath)!;
        directory = Path.GetFullPath(directory);
        if (!directory.StartsWith(rootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !string.Equals(directory, rootDirectory, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Create files inside the selected project.");
        var context = await ScaffoldProjectContext.TryEvaluateAsync(projectPath, token);
        var rootNamespace = context?.RootNamespace ?? project.DeclaredRootNamespace ?? Path.GetFileNameWithoutExtension(projectPath).Replace('-', '_');
        if (string.IsNullOrWhiteSpace(rootNamespace)) rootNamespace = Path.GetFileNameWithoutExtension(projectPath).Replace('-', '_');
        var relative = Path.GetRelativePath(rootDirectory, directory);
        var ns = rootNamespace + (relative == "." ? "" : "." + string.Join('.', relative.Split(Path.DirectorySeparatorChar).Select(SafeIdentifier)));
        if (ns.Split('.').Any(part => !Identifier().IsMatch(part) || Keywords.Contains(part))) throw new ArgumentException("The evaluated root namespace must contain valid C# identifiers.");
        var changes = new List<FileChange>();
        void Add(string file, string text) { var path = Path.Combine(directory, file); if (File.Exists(path)) throw new IOException($"{file} already exists."); changes.Add(new(path, "", text.Replace("\n", Environment.NewLine), $"Create {file}")); }
        var vmName = name.EndsWith("ViewModel") ? name : name + "ViewModel";
        if (kind is ScaffoldKind.ViewAndViewModel or ScaffoldKind.UserControl)
        {
            Add(name + ".xaml", ScaffoldTemplates.UserControlXaml(ns, name));
            Add(name + ".xaml.cs", ScaffoldTemplates.UserControlCode(ns, name));
        }
        if (kind is ScaffoldKind.ViewAndViewModel or ScaffoldKind.ViewModel)
        {
            Add(vmName + ".cs", ScaffoldTemplates.ViewModel(ns, vmName, context?.SupportsPartialProperties == true));
            changes.AddRange(await project.AddToolkitAsync(context, token));
        }
        if (kind == ScaffoldKind.ResourceDictionary) Add(name + ".xaml", ScaffoldTemplates.ResourceDictionary(name));
        if (kind == ScaffoldKind.Converter) Add(name + ".cs", ScaffoldTemplates.Converter(ns, name));
        if (addDataTemplate && kind == ScaffoldKind.ViewAndViewModel) Add(name + "Templates.xaml", ScaffoldTemplates.DataTemplates(ns, name, vmName));
        if (project.AddExplicitItems(changes.Where(c => c.Before.Length == 0).Select(c => c.Path), context) is { } items)
        {
            changes.RemoveAll(c => c.Path == projectPath);
            changes.Add(items);
        }
        return changes;
    }
    private static string SafeIdentifier(string value)
    {
        var safe = Regex.Replace(value, "[^A-Za-z0-9_]", "_");
        if (safe.Length == 0 || char.IsDigit(safe[0]) || Keywords.Contains(safe)) safe = "_" + safe;
        return safe;
    }
}
