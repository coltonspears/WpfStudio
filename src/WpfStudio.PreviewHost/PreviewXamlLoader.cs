using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

internal sealed record PreviewLoad(FrameworkElement Root, PreviewDocument? Document, PreviewBuildProvenance? Build, string? Status);

/// <summary>Creates the root a render shows, from the project's build or from the XAML buffer. Hosting it is the scene's job.</summary>
internal sealed class PreviewXamlLoader(PreviewAssemblyResolver assemblies, PreviewDiagnosticLog log)
{
    public PreviewLoad Load(PreviewRequest request, Assembly? assembly, PreviewScenarioActivation? activation, CancellationToken cancellationToken) =>
        request.Mode == PreviewMode.Compiled ? LoadCompiled(request, assembly, activation) : LoadSource(request, assembly, activation, cancellationToken);

    private static PreviewLoad LoadCompiled(PreviewRequest request, Assembly? assembly, PreviewScenarioActivation? activation)
    {
        if (assembly is null) throw new InvalidOperationException("Compiled preview requires a built project assembly.");
        var (root, build) = CompiledPreview.Create(request, assembly, activation);
        return new(root, null, build, $"Compiled view {build.ViewTypeName} · build {build.AssemblySha256[..12]}. Unsaved XAML is not included. " +
            (build.ApplicationResourcePath is null ? "Application resources were not requested. " : "Compiled application resources loaded. ") +
            "Project App constructor and startup are not executed.");
    }

    private PreviewLoad LoadSource(PreviewRequest request, Assembly? assembly, PreviewScenarioActivation? activation, CancellationToken cancellationToken)
    {
        if (assembly is not null)
        {
            bool loadedResources = CompiledPreview.PrepareApplication(request, assembly, optionalResources: activation is null);
            if (!string.IsNullOrWhiteSpace(request.ApplicationResourcePath))
                log.Add(new(loadedResources
                    ? $"Built application resources '{request.ApplicationResourcePath}' loaded; App construction and startup are not executed. Rebuild to update those resources."
                    : $"The build contains no '{request.ApplicationResourcePath}' application resource; source preview uses the view's own resources.", "Information"));
        }
        log.AddRange(assemblies.PrepareXamlNamespaces(request.Text));
        var document = PreviewDocument.Parse(request, assembly);
        log.AddRange(document.Diagnostics);
        // The secure source parse retains the original base URI and XML
        // positions, including attributes moved by preview transforms.
        using var markup = document.CreateSourceReader();
        object loaded = XamlReader.Load(markup);
        cancellationToken.ThrowIfCancellationRequested();
        if (loaded is ResourceDictionary resources)
            return new(new Border { Resources = resources, Child = new TextBlock { Text = $"Resource dictionary loaded · {resources.Count} resources", Margin = new Thickness(24), Foreground = Brushes.DimGray } },
                document, null, "Resources loaded. Open a view using this dictionary to see its appearance.");
        return new(loaded as FrameworkElement ?? throw new InvalidOperationException("The preview root must be a Window, Page, UserControl, FrameworkElement, or ResourceDictionary."), document, null, null);
    }
}
