using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

/// <summary>Explicit project factory calls, resolved and executed only inside this disposable host.</summary>
internal sealed class PreviewScenarioActivation
{
    private readonly PreviewScenario _configuration;
    private readonly Assembly _assembly;
    private readonly MethodInfo? _viewFactory;
    private readonly MethodInfo? _dataFactory;
    private BindingExpressionBase? _dataBinding;
    private FrameworkElement? _dataRoot;

    private PreviewScenarioActivation(PreviewScenario configuration, Assembly assembly, MethodInfo? viewFactory, MethodInfo? dataFactory)
    {
        _configuration = configuration; _assembly = assembly; _viewFactory = viewFactory; _dataFactory = dataFactory;
    }

    public bool HasViewFactory => _viewFactory is not null;

    public static PreviewScenarioActivation? Validate(PreviewRequest request, Assembly? assembly)
    {
        if (request.Scenario is not { } scenario) return null;
        ValidateName(scenario.Name, 128, "name");
        if (scenario.ViewFactory is null && scenario.DataContextFactory is null)
            throw new InvalidOperationException("A preview scenario must specify a view factory or a DataContext factory.");
        if (scenario.ViewFactory is not null && request.Mode != PreviewMode.Compiled)
            throw new InvalidOperationException("A scenario view factory requires Compiled preview mode. A DataContext factory can be used with source preview.");
        if (assembly is null) throw new InvalidOperationException("Preview scenarios require a built project assembly.");
        // Resolve both signatures before invoking either factory or constructing the view.
        MethodInfo? view = scenario.ViewFactory is null ? null : Resolve(assembly, scenario.ViewFactory, view: true);
        MethodInfo? data = scenario.DataContextFactory is null ? null : Resolve(assembly, scenario.DataContextFactory, view: false);
        return new(scenario, assembly, view, data);
    }

    private static MethodInfo Resolve(Assembly assembly, PreviewFactory factory, bool view)
    {
        ValidateName(factory.TypeName, 1024, "factory type");
        ValidateName(factory.MethodName, 256, "factory method");
        Type type = assembly.GetType(factory.TypeName, throwOnError: false, ignoreCase: false)
            ?? throw new InvalidOperationException($"The built assembly does not contain scenario factory type '{factory.TypeName}'.");
        if (!type.IsVisible || type.ContainsGenericParameters)
            throw new InvalidOperationException("Scenario factory types must be public and cannot have open generic parameters.");
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == factory.MethodName).ToArray();
        if (methods.Length != 1)
            throw new InvalidOperationException($"Scenario factory '{factory.TypeName}.{factory.MethodName}' must name exactly one declared method; missing or overloaded methods are unavailable.");
        MethodInfo method = methods[0];
        if (!method.IsPublic || !method.IsStatic || method.IsGenericMethod || method.GetParameters().Length != 0 || method.IsSpecialName ||
            method.ReturnType == typeof(void) || method.ReturnType.IsByRef || method.ReturnType.IsPointer || method.ReturnType.IsByRefLike || IsAsyncResult(method.ReturnType))
            throw new InvalidOperationException($"Scenario factory '{factory.TypeName}.{factory.MethodName}' must be a public static parameterless, non-generic synchronous method returning a value.");
        if (view && !typeof(FrameworkElement).IsAssignableFrom(method.ReturnType))
            throw new InvalidOperationException("A scenario view factory must declare a FrameworkElement return type, such as Window, Page, or UserControl.");
        return method;
    }

    private static void ValidateName(string? value, int maximumLength, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidOperationException($"The scenario {description} must be nonempty, have no surrounding whitespace or control characters, and be at most {maximumLength} characters.");
    }

    public FrameworkElement CreateView(Type? expectedType)
    {
        var view = (FrameworkElement)Invoke(_viewFactory!, "view", allowNull: false)!;
        if (view.Dispatcher != Dispatcher.CurrentDispatcher)
            throw new InvalidOperationException("The scenario view factory returned an element from another dispatcher. Create the view on the factory's WPF dispatcher.");
        if (expectedType is not null && !expectedType.IsInstanceOfType(view))
            throw new InvalidOperationException($"The scenario view factory returned '{view.GetType().FullName}', which is not assignable to expected view '{expectedType.FullName}'.");
        if (VisualTreeHelper.GetParent(view) is not null || LogicalTreeHelper.GetParent(view) is not null || PresentationSource.FromVisual(view) is not null)
            throw new InvalidOperationException("The scenario view factory returned an element that is already parented or displayed. Return a new, unhosted view.");
        return view;
    }

    public void ApplyDataContext(FrameworkElement view)
    {
        if (_dataFactory is null) return;
        object? value = Invoke(_dataFactory, "DataContext", allowNull: true);
        if (value is DispatcherObject dispatcherObject && dispatcherObject.Dispatcher != Dispatcher.CurrentDispatcher)
            throw new InvalidOperationException("The scenario DataContext factory returned an object from another dispatcher.");
        // Replacing the binding avoids writing through an authored TwoWay DataContext.
        _dataRoot = view;
        _dataBinding = BindingOperations.SetBinding(view, FrameworkElement.DataContextProperty,
            new Binding { Source = value, Mode = BindingMode.OneWay });
    }

    public bool IsDataContextProperty(DependencyObject target, DependencyProperty property) =>
        property == FrameworkElement.DataContextProperty && (ReferenceEquals(target, _dataRoot) || OwnsDataContext(target, property));

    public bool OwnsDataContext(DependencyObject target, DependencyProperty property)
    {
        if (property != FrameworkElement.DataContextProperty || _dataRoot is null) return false;
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        for (DependencyObject? current = target; current is not null && visited.Count < 128 && visited.Add(current);)
        {
            if (ReferenceEquals(current, _dataRoot))
                return ReferenceEquals(BindingOperations.GetBindingExpressionBase(current, property), _dataBinding);
            if (DependencyPropertyHelper.GetValueSource(current, property).BaseValueSource != BaseValueSource.Inherited) return false;
            current = current is FrameworkElement element ? element.Parent ?? VisualTreeHelper.GetParent(element)
                : current is FrameworkContentElement content ? content.Parent : null;
        }
        return false;
    }

    public PreviewScenarioProvenance Provenance(PreviewRequest request, FrameworkElement view, PreviewBuildProvenance? build) =>
        new(_configuration, Path.GetFullPath(request.AssemblyPath!),
            build?.AssemblySha256 ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_assembly.Location))),
            _assembly.ManifestModule.ModuleVersionId.ToString("D"), view.GetType().FullName ?? view.GetType().Name);

    private object? Invoke(MethodInfo method, string kind, bool allowNull)
    {
        object? value;
        try { value = method.Invoke(null, null); }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Scenario '{_configuration.Name}' {kind} factory failed: {exception.GetBaseException().Message}");
        }
        if (value is null && !allowNull) throw new InvalidOperationException($"Scenario '{_configuration.Name}' {kind} factory returned null.");
        if (value is not null && IsAsyncResult(value.GetType())) throw new InvalidOperationException($"Scenario '{_configuration.Name}' {kind} factory returned an asynchronous result. Only synchronous factories are supported.");
        return value;
    }

    private static bool IsAsyncResult(Type type) => typeof(Task).IsAssignableFrom(type) || type == typeof(ValueTask) ||
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>) ||
        type.GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is not null;
}
