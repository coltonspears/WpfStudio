# Preview data and scenarios

The designer can show project-defined states without starting the application. Open a XAML view, build its project, choose a **Scenario**, and press **Refresh**. **Default** uses the view's own data and any enabled design-time declarations. Selecting a scenario invalidates the previous preview; it does not execute a factory until refresh.

The bundled CounterApp includes **Initial**, **Counted**, and **Large count** scenarios for `MainWindow.xaml`. Build CounterApp first, then open Preview, select a state, expand **Preview settings**, clear **App resources**, and Refresh. Its empty `App.xaml` resource section does not produce a compiled application resource; its view loads `Themes/Colors.xaml` directly. The files `CounterPreviewScenarios.cs` and `wpfstudio.preview.json` provide a small working example.

## Declare named states

Place `wpfstudio.preview.json` beside the project file. Paths are relative to that directory; `../Shared/View.xaml` can identify a linked file. For linked views, select the owning **Project** in the editor before opening Preview.

```json
{
  "version": 1,
  "views": [
    {
      "path": "Views/OrdersView.xaml",
      "scenarios": [
        {
          "name": "Loading",
          "dataContextFactory": {
            "typeName": "MyApp.PreviewData",
            "methodName": "Loading"
          }
        },
        {
          "name": "Populated",
          "dataContextFactory": {
            "typeName": "MyApp.PreviewData",
            "methodName": "Populated"
          }
        }
      ]
    }
  ]
}
```

Factories are explicitly named public static, parameterless, synchronous methods declared on public types in the built project assembly. No WpfStudio package reference or special base class is required. For example:

```csharp
namespace MyApp;

public static class PreviewData
{
    public static OrdersViewModel Loading() => new() { IsLoading = true };
    public static OrdersViewModel Populated() => new()
    {
        Orders = [new Order { Customer = "Ada", Total = 42 }]
    };
}
```

These example model members belong to the application. The IDE does not infer them or manufacture Loading/Empty/Error states. A data factory may return null to preview an explicitly empty DataContext. Each factory result replaces the root DataContext before presentation and layout through a OneWay binding; it does not write through an existing TwoWay DataContext binding. Descendants retain their own local DataContexts and normal inheritance rules.

Save configuration changes, then use **Reload scenarios** or **Refresh**. Duplicate names, `Default` as a named scenario, duplicate view paths, unknown fields, malformed JSON and unsupported schema versions produce explanations. Invalid configuration never yields a partially populated catalog. Configuration is bounded to 256 KiB, 128 views, 64 scenarios per view and 512 scenarios overall. Comments and trailing commas are accepted. Discovery only reads JSON; it never loads application assemblies into the IDE.

## Views with constructor dependencies

Add a `viewFactory` and use **Compiled** mode when a view needs services or a DI container:

```json
{
  "name": "Offline services",
  "viewFactory": {
    "typeName": "MyApp.PreviewData",
    "methodName": "CreateOrdersView"
  },
  "dataContextFactory": {
    "typeName": "MyApp.PreviewData",
    "methodName": "Populated"
  }
}
```

```csharp
public static OrdersView CreateOrdersView()
{
    var service = new InMemoryOrderService();
    return new OrdersView(service);
}
```

The factory may compose the application's own services. WpfStudio does not infer or run the application's startup/DI pipeline. Return a new unparented `FrameworkElement` created on the factory's WPF dispatcher, compatible with the selected **View type**. The declared return type must also derive from `FrameworkElement`. Overloaded, generic, private, instance, parameterized and asynchronous factories are rejected explicitly. A null view is an error. Both factory signatures are checked before either factory is invoked.

The **App resources** setting under **Preview settings** applies to compiled previews and named source scenarios. Its default is `App.xaml`; clear it for a library or view without application resources. The host loads the compiled resource dictionary without constructing the project's `App`. Source mode still uses the current XAML buffer and keeps its source map; compiled view factories use the built view and do not claim current-buffer source locations. Rebuild after changing factory or view code.

Each scenario render starts a fresh isolated host, including source mode. Returning to Default also starts a fresh host after a scenario. The active name, assembly hash and build provenance distinguish the accepted result from a merely selected scenario. Configuration fingerprints, document revisions and selection guards reject stale renders, inspections and source-edit reviews. Timeouts/cancellation terminate the owned host so a hung factory cannot block the IDE. Process isolation is not a security sandbox: explicitly selected factories and their application dependencies can have external effects.

## Design-time XAML values

With **Design-time values** enabled in Source mode, supported declarations such as `d:Text`, `d:Content`, `d:Visibility`, sizing hints, and explicit design property elements affect only the preview:

```xml
<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
           xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
           xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
           mc:Ignorable="d"
           Text="{Binding Customer.Name}" d:Text="Ada Lovelace" />
```

The implementation recognizes the namespace URI rather than requiring the `d` prefix. It applies verified writable dependency-property literals and explicit property elements such as `d:ListView.ItemsSource` containing an `x:Array`. `d:DesignWidth`/`d:DesignHeight` provide hints when an explicit size is absent or Auto. Unsupported members, conflicts, design-only controls and unsupported markup extensions have source-located diagnostics; no guessed runtime values are substituted.

The Properties inspector identifies these declarations as a **design baseline**, alongside the observed property-system source (for example, **Local (design baseline)**). Application callbacks can subsequently replace the effective value, so the label describes the declared baseline rather than claiming that it still supplies the current value. A replaced binding is not evaluated in that preview, and a diagnostic explains this; source language diagnostics remain based on the original XAML. Design-only properties cannot be written back as runtime XAML values. Disabling the toggle restores the authored runtime declarations on the next refresh. A selected data factory takes precedence over a root design DataContext; nested local contexts still apply.

`d:DesignInstance`, `CreateList`, and `d:DesignData` are not instantiated by the preview in this increment. Their faux-type, real-construction and sample-file semantics require further support. Language-service use of a declared design type remains separate from constructing a preview object. This distinction follows the documented [design-time data model](https://learn.microsoft.com/en-us/visualstudio/xaml-tools/xaml-designtime-data?view=visualstudio).

The preview host currently targets .NET 10 on Windows. Scenarios do not establish other-runtime/architecture fidelity, native input fidelity, or application-startup equivalence.
