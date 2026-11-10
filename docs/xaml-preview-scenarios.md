# Preview data and scenarios

The designer can show project-defined states without starting the application. Open a XAML view, build its project, choose a **Scenario**, and press **Refresh**. **Default** uses the view's own data and its [design-time data](#design-time-data): a `d:DataContext`, or sample values generated from the view model the view is named for. Selecting a scenario invalidates the previous preview; it does not execute a factory until refresh.

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

The **App resources** setting under **Preview settings** applies to ordinary source previews, compiled previews and named source scenarios. Its default is `App.xaml`; clear it to omit application resources. The host loads the compiled resource dictionary without constructing the project's `App`. An ordinary source preview can continue when no matching application resource was compiled, reporting that it uses the view's own resources; malformed resources still fail. Compiled previews and named scenarios require the selected resource. Source mode still uses the current XAML buffer and keeps its source map; compiled view factories use the built view and do not claim current-buffer source locations. Rebuild after changing application resources, factory or view code.

Loading application resources does not run startup or dependency injection. If the root DataContext is absent and bindings have unavailable sources, the preview explains that in its status. Declare [design-time data](#design-time-data), or supply a data factory such as the examples above, to preview application data. Individual binding diagnostics remain inspectable.

Each scenario render starts a fresh isolated host, including source mode. Returning to Default also starts a fresh host after a scenario. The active name, assembly hash and build provenance distinguish the accepted result from a merely selected scenario. Configuration fingerprints, document revisions and selection guards reject stale renders, inspections and source-edit reviews. Timeouts/cancellation terminate the owned host so a hung factory cannot block the IDE. Process isolation is not a security sandbox: explicitly selected factories and their application dependencies can have external effects.

## Design-time XAML values

With **Design-time values** (the **d:** toggle) enabled in Source mode, `d:` declarations affect only the preview:

```xml
<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
           xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
           xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
           mc:Ignorable="d"
           Text="{Binding Customer.Name}" d:Text="Ada Lovelace" />
```

The implementation recognizes the namespace URI rather than requiring the `d` prefix.

- **A literal** such as `d:Text`, `d:Visibility` or `d:Margin` is checked against the dependency property before it is applied.
- **A markup extension** is evaluated by the loader exactly as it would be on the runtime attribute: `d:Text="{Binding Summary}"`, `d:Background="{StaticResource AccentBrush}"`, `d:DataContext="{x:Static local:DesignData.Orders}"`.
- **A property element** such as `d:ListView.ItemsSource` containing an `x:Array`, or `d:Grid.DataContext` containing an object, is applied as written.
- `d:DesignWidth` and `d:DesignHeight` size the artboard when the root has no explicit size, or Auto.
- `d:IsHidden="True"` hides an element in the preview, as the eye toggle of other designers does, and `d:IsEffectDisabled="True"` removes its effect. `d:DesignSource` fills a `CollectionViewSource`.
- Designer bookkeeping such as `d:IsLocked` and `d:LayoutOverrides` is ignored without comment.

Unsupported members, conflicting declarations and design-only controls have source-located diagnostics; no guessed runtime value is substituted.

If a render fails while design-time markup is applied (a resource that does not exist, a converter that throws), the preview renders once more without any design-time values and reports the failure as a warning. A design-time declaration can add to the preview but cannot take the authored view away.

The Properties inspector identifies these declarations as a **design baseline**, alongside the observed property-system source (for example, **Local (design baseline)**). Application callbacks can subsequently replace the effective value, so the label describes the declared baseline rather than claiming that it still supplies the current value. A replaced binding is not evaluated in that preview, and a diagnostic explains this; source language diagnostics remain based on the original XAML. Design-only properties cannot be written back as runtime XAML values. Disabling the toggle restores the authored runtime declarations on the next refresh.

## Design-time data

`d:DataContext` gives a view data to draw with, without starting the application:

```xml
<UserControl x:Class="Shop.Views.OrdersView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:vm="clr-namespace:Shop.ViewModels"
             mc:Ignorable="d"
             d:DataContext="{d:DesignInstance Type=vm:OrdersViewModel}">
```

Build the project first: the preview reads the type from the built assembly. The declarations it understands:

| Declaration | What the preview draws with |
| --- | --- |
| `{d:DesignInstance Type=vm:OrdersViewModel}` | Sample values generated from the type. No constructor runs. |
| `{d:DesignInstance Type=vm:OrdersViewModel, IsDesignTimeCreatable=True}` | The instance the type's public parameterless constructor builds. If construction fails, a warning gives the reason and generated sample values are shown instead. |
| `{d:DesignInstance Type=vm:Order, CreateList=True}` | A list of five sample items, or of three constructed ones with `IsDesignTimeCreatable=True`. On a list property such as `d:ItemsSource`, an item type means a list without `CreateList`. |
| `{d:DesignData Source=/SampleData/Orders.xaml}` | The objects a XAML file in the project declares. A leading slash starts at the project folder; any other path starts beside the view. The file's objects are constructed and their properties set, as the `DesignDataWithDesignTimeCreatableTypes` build action does. Only a file inside the project is read. |
| `d:ItemsSource="{d:SampleData ItemCount=5}"` | Items with no declared type. Each answers whatever the item template binds to, by property name, so `{Binding Name}`, `{Binding Author.Email}` and `{Binding Price, StringFormat=C}` all show something plausible. |
| `{x:Static …}`, `{StaticResource …}`, `{Binding …}` | Whatever the expression evaluates to. |

A declaration that cannot be honored (a type that is not in the build, a missing file, an unknown option) is reported on its line and the authored runtime value stays. A selected scenario's data factory takes precedence over the root's design-time data; nested local contexts still apply. Inside a template, a `d:DataContext` shape on the template or on one of its elements describes the items for the editor and is not applied: the list or the templated control supplies them.

The toolbar names the data in use beside the **d:** toggle, for example `OrdersViewModel · sample data`. Its tooltip lists every design-time data source of the preview with its line.

### Generated sample values

Most view models cannot be constructed by a designer: their constructors take services, or fetch data. For `d:DesignInstance` without `IsDesignTimeCreatable`, Visual Studio substitutes a stand-in type with the same properties, all at their default values, so the view is typed but empty. WpfStudio fills the real type instead:

- The object is allocated **without running any constructor**, and field initializers do not run either. No service is resolved and no data is loaded.
- The field behind each public property receives a value: an auto-property's own field, or a field named after the property by a common convention (`_title`, `title`, `m_title`, as `[ObservableProperty]` and hand-written change notification use). A property with a public setter and no such field is set through its setter.
- Computed properties are not touched. They read the filled fields, so `Total => Orders.Sum(o => o.Amount)` and `HasOrders => Orders.Count > 0` show values that agree with the list beside them.
- Values follow each property's name and type. `Email` is an address, `Price` is money, `CreatedAt` is a recent date, `IsBusy` and `HasErrors` are false while `HasItems` and `CanSave` are true, an enum takes its first meaningful member, and an `ICommand` is a command that does nothing, so its button draws enabled. An `ImageSource` is a placeholder drawing, and a string or `Uri` named for a picture (`PhotoUrl`, `Thumbnail`) is the location of a placeholder image compiled into the preview host.
- Collections receive five items, nested collections fewer, to a depth of four. A project type used as a property is filled the same way. `SelectedOrder` beside `Orders` is the first of those orders, so the list shows its selection and the details match it.
- For an interface or an abstract class, the project's class that implements it is filled. When a `d:DesignInstance` names one that nothing implements, its bindings are answered by property name, as for `d:SampleData`.
- `object`, delegates, UI objects and the types of the framework itself stay empty. A guess there would mislead more than an empty value.
- The same name at the same position always gives the same value, so a preview does not change from one render to the next.

What still runs is the project's own getters, and a setter where no field could be found. A getter that depends on something the constructor would have created fails as a binding error in the diagnostics, exactly as an exception in a getter does at run time. A project's own collection class is the one kind of project type that is constructed (through its parameterless constructor), because it has no storage to add items to otherwise. Use `IsDesignTimeCreatable=True`, a `d:DesignData` file or a [scenario](#declare-named-states) when the data must be exact.

### A view with no DataContext

Many views declare no DataContext at all: code-behind or a container assigns it, and a source preview runs neither. When the root has no `DataContext` and no `d:DataContext`, the preview looks in the built project, and then in the libraries it references, for the view model the view's `x:Class` name pairs it with:

| View | View model |
| --- | --- |
| `OrdersView`, `OrdersPage`, `OrdersWindow`, `OrdersDialog`, `OrdersControl` | `OrdersViewModel`, or the full name with the suffix, such as `OrdersPageViewModel` |
| `MainWindow` | `MainWindowViewModel` or `MainViewModel` |
| `Shell` | `ShellViewModel` or `ShellVM` |

One best match is filled with generated sample values and becomes the root DataContext; an information diagnostic names the type, and the toolbar shows it as `OrdersViewModel · sample data, matched by name`. When several types match equally well, nothing is chosen and the diagnostic lists them. Declaring `d:DataContext` always decides the matter. Turn the matching off under **Preview settings** with **Sample data for a view with no DataContext**.

This matching feeds the preview only. Completion and binding diagnostics in the editor describe declared types and never a type guessed from a name. **Declare**, beside the data label, makes the match the author's choice: it writes `d:DataContext="{d:DesignInstance Type=vm:OrdersViewModel}"` on the root element, with the namespace mappings it needs and `mc:Ignorable`, as one edit you review before it is applied to the editor buffer. From then on the editor completes and checks the view's bindings too.

### Editor support

A `d:DataContext` types the bindings beneath it for completion, hover, **F12** and missing-member diagnostics:

- `{d:DesignInstance …}` gives the named type, or a list of it with `CreateList=True`.
- `{x:Static local:DesignData.Orders}` gives the declared type of the static property or field. A member declared as `object` gives none.
- `{StaticResource key}` gives the type of the resource.
- `{Binding Path}` is followed from the context entering the element.

`d:DesignData` and `d:SampleData` have no shape the editor can know without loading them, so bindings beneath them are neither completed nor diagnosed.

Scenarios run on the host chosen for the build: [.NET Framework projects](xaml-preview-dependencies.md#net-framework-projects) on .NET Framework, everything else on .NET 10. They do not establish native input fidelity or application-startup equivalence.
