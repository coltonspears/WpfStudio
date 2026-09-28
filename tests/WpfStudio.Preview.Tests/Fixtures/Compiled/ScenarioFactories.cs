using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfStudio.PreviewFixture;

public static class ScenarioFactories
{
    private static int _invocations;
    public static object Loading() => new ScenarioData("Loading orders", 0);
    public static object Empty() => new ScenarioData("No orders", 0);
    public static object Populated() => new ScenarioData("3 orders", 3);
    public static object Error() => new ScenarioData("Unable to load orders", 0);
    public static DependencyView CreateView() => new(new PreviewOrderService());
    public static object InvocationCounter() => new ScenarioData("Invocation " + ++_invocations, 0);
    public static object NullData() => null!;
    public static DependencyView NullView() => null!;
    public static FrameworkElement WrongView() => new Button();
    public static object ThrowData() => throw new InvalidOperationException("Deliberate scenario failure");
    public static DependencyView ThrowView() => throw new InvalidOperationException("The view factory must not run before all metadata validates");
    public static object HangingData()
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"WpfStudio.Scenario.Hang.{Environment.ProcessId}.marker"), "Entered factory");
        Thread.Sleep(Timeout.Infinite); return null!;
    }
    public static Task<ScenarioData> AsyncData() => Task.FromResult(new ScenarioData("Async", 0));
    public static object DisguisedAsyncData() => Task.FromResult(new ScenarioData("Async", 0));
    public static object GenericData<T>() => new ScenarioData(typeof(T).Name, 0);
    public static object ParameterData(string title) => new ScenarioData(title, 0);
    public static object OverloadedData() => new ScenarioData("Overload", 0);
    public static object OverloadedData(string title) => new ScenarioData(title, 0);
    private static object PrivateData() => new ScenarioData("Private", 0);
    public static void VoidData() => throw new InvalidOperationException("Void factory should not run");
    public static DependencyView ParentedView()
    {
        var view = CreateView();
        _ = new Border { Child = view };
        return view;
    }
    public static FrameworkElement WrongDispatcherView()
    {
        FrameworkElement? view = null;
        var thread = new Thread(() => view = new Button());
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        return view!;
    }
    public static object WrongDispatcherData()
    {
        DependencyObject? data = null;
        var thread = new Thread(() => data = new DependencyObject());
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return data!;
    }
    public static ScenarioTwoWayView CreateTwoWayView() => new();
    public static object TwoWayData() => new TwoWayScenarioData();
}

public sealed class InstanceScenarioFactories
{
    public object CreateData() => new ScenarioData("Instance", 0);
}

public sealed class ScenarioCounterControl : TextBlock
{
    public ScenarioCounterControl() => Text = ((ScenarioData)ScenarioFactories.InvocationCounter()).Title;
}

public sealed class DesignOverwriteControl : TextBlock
{
    public DesignOverwriteControl() => Loaded += (_, _) =>
    {
        if (Tag is "clear") ClearValue(TextProperty);
        else Text = "Loaded replacement";
    };
}

public sealed class ScenarioTwoWayView : UserControl
{
    public ScenarioTwoWayView()
    {
        BindingOperations.SetBinding(this, DataContextProperty, new Binding(nameof(OriginalScenarioSource.Value))
        { Source = OriginalScenarioSource.Instance, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var message = new TextBlock { Name = "ScenarioMessage" };
        message.SetBinding(TextBlock.TextProperty, new Binding(nameof(TwoWayScenarioData.Title)));
        var writes = new TextBlock { Name = "SourceWrites" };
        writes.SetBinding(TextBlock.TextProperty, new Binding(nameof(TwoWayScenarioData.SourceWrites)));
        Content = new StackPanel { Children = { message, writes } };
    }
}

public sealed class OriginalScenarioSource
{
    public static OriginalScenarioSource Instance { get; } = new();
    private object _value = new TwoWayScenarioData();
    public int Writes { get; private set; }
    public object Value { get => _value; set { Writes++; _value = value; } }
}
public sealed class TwoWayScenarioData
{
    public string Title => "Scenario overrides binding";
    public int SourceWrites => OriginalScenarioSource.Instance.Writes;
}
