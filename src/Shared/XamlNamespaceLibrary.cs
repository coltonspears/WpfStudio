namespace WpfStudio.PreviewDependencies;

/// <summary>The XAML namespaces an assembly declares through XmlnsDefinition and XmlnsCompatibleWith, read from metadata.</summary>
internal sealed record XamlNamespaceLibrary(string Path, string Name, IReadOnlyList<string> Namespaces, IReadOnlyList<KeyValuePair<string, string>> Compatible);
