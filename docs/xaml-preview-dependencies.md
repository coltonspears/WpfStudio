# Preview dependencies in multi-project solutions

Build the project that owns the XAML view, including its project references, before previewing custom controls. For a linked XAML file, choose its owning **Project** in the editor. That project's evaluated output assembly and restore metadata determine which libraries the preview uses.

The preview copies the built output into its isolated process's temporary directory. WPF control libraries often leave transitive NuGet packages out of their output directory. The designer now fills those gaps using the built `.deps.json` and the package folders in the project's evaluated `ProjectAssetsFile`. It selects the exact package versions and compatible runtime assets recorded by the build. Existing output files take precedence over package-cache copies, and compiler reference assemblies are excluded.

## HintPath and file references

Many WPF solutions reference vendor control suites and shared libraries by file, through a `<Reference>` with a `HintPath`, rather than through NuGet. Such references are often marked `<Private>false</Private>`, so the build never copies them into the output. When the designer opens a view and on every **Refresh**, it asks MSBuild which assemblies the owning project references, in the same way a build resolves them. It runs `ResolveAssemblyReferences` with the selected configuration and target framework; nothing is compiled. The preview then stages:

- HintPath and other file references, including `Private=false` ones.
- Assemblies those references depend on that MSBuild finds beside them (for example `Vendor.Core.dll` next to `Vendor.Controls.dll`), and their satellite resource assemblies.
- The output of referenced projects, and their own HintPath references and dependencies. Project references are followed up to 32 projects deep; `ReferenceOutputAssembly="false"` references are skipped.

Files already in the build output win over the original HintPath copy. Framework and NuGet reference assemblies are ignored, because packages come from the runtime manifest described above. Each folder a HintPath resolved from is also searched when a control loads an assembly late, such as a plugin, a design-time part or a native DLL. Rebuilding or adding a DLL in one of those folders replaces the preview process on the next render. Network-share paths are not loaded; copy those libraries to a local folder.

Typing reuses the last resolved references; choose **Refresh** after editing the project file. If MSBuild cannot evaluate a project, for example before restore, the status bar shows the reason and the preview continues with the build output alone.

Both explicit CLR namespaces and libraries that publish an XML namespace through `XmlnsDefinition` are supported on a cold source preview. For example:

```xml
xmlns:controls="clr-namespace:MyControls;assembly=MyControls"
xmlns:vendor="urn:my-controls"
```

Recognizing these types before loading source lets the designer omit code-behind event handlers and preserve element source locations. Custom control constructors and markup extensions still execute inside the preview process.

Referenced resource dictionaries can use either form:

```xml
<ResourceDictionary Source="/MyControls;component/Themes/Colors.xaml" />
<ResourceDictionary Source="pack://application:,,,/MyControls;component/Themes/Colors.xaml" />
```

Ordinary relative dictionary sources still resolve beside the authored XAML. The designer does not change the document to normalize a pack URI.

After rebuilding a dependency, choose **Refresh**. The next render checks output files, runtime manifests, restore metadata and the selected package files, and replaces the preview process when those inputs change. This also happens during the next live source render. Ordinary XAML edits reuse a healthy process. Loaded libraries come from the temporary copy so project outputs and package files remain available to rebuild or restore.

If a runtime package is absent from both the output and the recorded package folders, its diagnostic names the missing dependency and expected location. Restore and rebuild the selected project, then refresh. The designer does not search arbitrary solution outputs or silently substitute another package version. The same diagnostic is shown when a library is missing from the output, the HintPath folders and the package cache.

Ordinary source previews also load the built `App.xaml` resource dictionary by default, including merged theme dictionaries. The current view remains source-loaded, while application resources come from the last build; rebuild after editing those resources. **App resources** in preview settings changes or clears that path. A library without a compiled application resource can still render its own resources. Source Windows retain their real Window ancestry for attached behaviors and relative bindings.

This covers SDK-style builds with local outputs inside the selected project directory. The preview host currently runs on .NET 10 Windows x64; dependency discovery does not change that runtime or architecture. Application startup and dependency-injection setup are not executed. For a view whose DataContext normally comes from startup, supply [preview scenario data](xaml-preview-scenarios.md).

## Reproduce a real solution locally

The loaded-shell test can open an existing solution and view through the same workspace and designer commands as the app. Build WpfStudio's tests first, then set the two paths for a previously built application view:

```powershell
$env:WPFSTUDIO_TEST_PREVIEW_SOLUTION = 'C:\Work\MyApp\MyApp.slnx'
$env:WPFSTUDIO_TEST_PREVIEW_DOCUMENT = 'C:\Work\MyApp\Views\MainWindow.xaml'
try {
    dotnet test tests/WpfStudio.App.Tests/WpfStudio.App.Tests.csproj -c Release --no-build
} finally {
    Remove-Item Env:WPFSTUDIO_TEST_PREVIEW_SOLUTION, Env:WPFSTUDIO_TEST_PREVIEW_DOCUMENT
}
```

This opt-in check loads both source and compiled previews, writes diagnostics and real rendered captures under `artifacts/external-preview`, and does not edit or save the target source. Compiled mode invokes the view constructor as usual; neither mode starts the project's `App`. The shell uses an isolated temporary settings directory. `WPFSTUDIO_PREVIEW_HOST_UNDER_TEST` and `WPFSTUDIO_TEST_WORKSPACE_HOST` can point to the exact packaged host executable and workspace DLL. Captures and observations stay local unless explicitly copied into documentation.
