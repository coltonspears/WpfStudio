using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Diagnostics;
using System.Windows.Media;

namespace WpfStudio.InspectionFixture;

public partial class BindingSourceProbeView : UserControl
{
    public BindingSourceProbeView()
    {
        InitializeComponent();
        DataContext = new BindingSourceProbeModel();
        // A resource lookup yields a Binding object; assigning that object to
        // Text via StaticResource does not call Binding.ProvideValue again.
        ResourcePrimary.SetBinding(TextBlock.TextProperty, (Binding)Resources["ProbeSharedBinding"]);
        CodePrimary.SetBinding(TextBlock.TextProperty, new Binding(nameof(BindingSourceProbeModel.Primary)));
    }
}

public sealed class BindingSourceProbeModel
{
    public static int GetterCalls;
    public string Primary { get { GetterCalls++; return "Primary"; } }
    public string Secondary { get { GetterCalls++; return "Secondary"; } }
}

// A fixture-only probe of documented WPF object identity/source APIs. No agent
// contract or production navigation behavior depends on the exploratory output.
internal sealed class BindingSourceProbeFixture(string directory, PrimaryWindow primary)
{
    private sealed record Identity(int Id);
    private readonly ConditionalWeakTable<object, Identity> _identities = new();
    private int _nextIdentity;
    private BindingSourceProbeView? _view;

    public void Apply(string command)
    {
        if (command == "binding-source-probe-create" && _view is null)
        {
            _view = new BindingSourceProbeView();
            primary.PrimaryContent.Children.Add(_view);
            _view.TemplateOwner.ApplyTemplate();
            primary.UpdateLayout();
        }
        if (command == "binding-source-probe-replace" && _view is not null)
            _view.InlinePrimary.SetBinding(TextBlock.TextProperty, new Binding(nameof(BindingSourceProbeModel.Primary)));
        if (command == "binding-source-probe-large" && _view is not null)
        {
            var composite = new MultiBinding { StringFormat = "{0}" };
            for (int i = 0; i < 100; i++) composite.Bindings.Add(new Binding(nameof(BindingSourceProbeModel.Primary)));
            _view.CodePrimary.SetBinding(TextBlock.TextProperty, composite);
        }
        if (command == "binding-source-probe-unload" && _view is not null)
            primary.PrimaryContent.Children.Remove(_view);
        if (command == "binding-source-probe-state")
            File.WriteAllText(Path.Combine(directory, "binding-source-state.json"), JsonSerializer.Serialize(new { BindingSourceProbeModel.GetterCalls }));
        if (command != "binding-source-probe-dump" || _view is null) return;

        int before = BindingSourceProbeModel.GetterCalls;
        var rows = new List<object>();
        Read("Inline", _view.InlinePrimary, TextBlock.TextProperty);
        Read("InlineSameLineOtherProperty", _view.InlinePrimary, FrameworkElement.TagProperty);
        Read("InlineSameLineSamePath", _view.InlinePrimary, FrameworkElement.ToolTipProperty);
        Read("InlineEntity", _view.EntityPrimary, TextBlock.TextProperty);
        Read("InlineMultiline", _view.MultilinePrimary, TextBlock.TextProperty);
        Read("Object", _view.ObjectPrimary, TextBlock.TextProperty);
        Read("StyleSetter", _view.StylePrimary, TextBlock.TextProperty);
        Read("SharedStyleSetter", _view.StyleShared, TextBlock.TextProperty);
        if (_view.TemplateOwner.Template.FindName("TemplatePrimary", _view.TemplateOwner) is TextBlock template)
            Read("Template", template, TextBlock.TextProperty);
        if (FindDataTemplateChild(_view.DataTemplateOwner) is { } dataTemplate)
            Read("DataTemplate", dataTemplate, TextBlock.TextProperty);
        Read("Multi", _view.MultiPrimary, TextBlock.TextProperty);
        Read("Priority", _view.PriorityPrimary, TextBlock.TextProperty);
        Read("Resource", _view.ResourcePrimary, TextBlock.TextProperty);
        Read("Code", _view.CodePrimary, TextBlock.TextProperty);
        File.WriteAllText(Path.Combine(directory, "binding-source-probe.json"), JsonSerializer.Serialize(new
        {
            Runtime = Environment.Version.ToString(), GetterCallsBefore = before,
            GetterCallsAfter = BindingSourceProbeModel.GetterCalls, Rows = rows
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Read(string form, DependencyObject target, DependencyProperty property)
        {
            var expression = BindingOperations.GetBindingExpressionBase(target, property);
            if (expression is null)
            {
                rows.Add(new { Form = form, Error = "No BindingExpressionBase is attached." });
                return;
            }
            int remaining = 65;
            Walk(expression, null, null, ref remaining);
            void Walk(BindingExpressionBase current, int? parentId, int? index, ref int budget)
            {
                if (--budget < 0) return;
                int id = GetId(current);
                var binding = current.ParentBindingBase;
                rows.Add(new
                {
                    Form = form, Node = (target as FrameworkElement)?.Name, Property = property.Name,
                    ExpressionId = id, BindingObjectId = GetId(binding), ParentExpressionId = parentId,
                    ChildIndex = index, Kind = binding.GetType().Name, Status = current.Status.ToString(),
                    Path = (binding as Binding)?.Path?.Path,
                    BindingSource = Source(binding), ExpressionSource = Source(current), TargetSource = Source(target)
                });
                var children = current is MultiBindingExpression multi ? multi.BindingExpressions
                    : current is PriorityBindingExpression priority ? priority.BindingExpressions : null;
                if (children is null) return;
                for (int child = 0; child < children.Count && budget > 0; child++) Walk(children[child], id, child, ref budget);
            }
        }
    }

    private int GetId(object value) => _identities.GetValue(value, _ => new(++_nextIdentity)).Id;
    private static TextBlock? FindDataTemplateChild(DependencyObject owner)
    {
        if (owner is TextBlock { Name: "DataTemplatePrimary" } child) return child;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(owner); i++)
            if (FindDataTemplateChild(VisualTreeHelper.GetChild(owner, i)) is { } result) return result;
        return null;
    }
    private static object? Source(object value)
    {
        var source = VisualDiagnostics.GetXamlSourceInfo(value);
        return source is null ? null : new { Uri = source.SourceUri?.OriginalString, Line = source.LineNumber, Column = source.LinePosition };
    }
}
