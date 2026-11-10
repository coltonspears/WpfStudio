using System.ComponentModel;
using System.Dynamic;

namespace WpfStudio.PreviewHost;

/// <summary>
/// A sample object with no declared shape, for <c>d:SampleData</c> and for lists whose item type is unknown. It
/// answers a binding to any property with a value suited to the property's name, and remembers what is written to it,
/// so an item template shows plausible content whatever it binds to.
/// </summary>
public sealed class DesignSampleItem : DynamicObject, INotifyPropertyChanged
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly int _index;
    private readonly int _depth;
    private readonly string _text;
    private readonly string? _member;

    /// <param name="member">The member of another sample item that this one is the value of, if any.</param>
    internal DesignSampleItem(int index, int depth = 0, string? text = null, string? member = null)
    {
        _index = index; _depth = depth; _member = member;
        _text = text ?? "Sample item " + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        if (!_values.TryGetValue(binder.Name, out result)) _values[binder.Name] = result = DesignSampleValues.Untyped(binder.Name, _member, _index, _depth);
        return true;
    }

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        _values[binder.Name] = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(binder.Name));
        return true;
    }

    public override IEnumerable<string> GetDynamicMemberNames() => _values.Keys;

    /// <summary>The text this item stands for, shown wherever it is bound without a further path.</summary>
    public override string ToString() => _text;

    // A trigger or a selection that compares the bound value with text sees the text, as it would for a string property.
    public override bool Equals(object? obj) => obj is string text ? text == _text : ReferenceEquals(this, obj);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_text);
}
