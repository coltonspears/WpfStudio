using System.Xml;

namespace WpfStudio.PreviewHost;

/// <summary>
/// A zero line tells WPF to retain the preceding node's position. Give synthetic
/// preview nodes a positive out-of-document line instead, so their created
/// objects cannot borrow an authored binding's location.
/// </summary>
internal sealed class OriginalPositionXmlReader(XmlReader reader) : XmlReader, IXmlLineInfo
{
    private IXmlLineInfo? Original => reader as IXmlLineInfo;
    public bool HasLineInfo() => true;
    public int LineNumber => Original is { } original && original.HasLineInfo() && original.LineNumber > 0
        ? original.LineNumber : int.MaxValue;
    public int LinePosition => Original is { } original && original.HasLineInfo() && original.LinePosition > 0
        ? original.LinePosition : 1;
    public override int AttributeCount => reader.AttributeCount;
    public override string BaseURI => reader.BaseURI;
    public override int Depth => reader.Depth;
    public override bool EOF => reader.EOF;
    public override bool HasValue => reader.HasValue;
    public override bool IsEmptyElement => reader.IsEmptyElement;
    public override string LocalName => reader.LocalName;
    public override string NamespaceURI => reader.NamespaceURI;
    public override XmlNameTable NameTable => reader.NameTable;
    public override XmlNodeType NodeType => reader.NodeType;
    public override string Prefix => reader.Prefix;
    public override ReadState ReadState => reader.ReadState;
    public override string Value => reader.Value;
    public override string Name => reader.Name;
    public override string XmlLang => reader.XmlLang;
    public override XmlSpace XmlSpace => reader.XmlSpace;
    public override char QuoteChar => reader.QuoteChar;
    public override bool CanResolveEntity => reader.CanResolveEntity;
    public override XmlReaderSettings? Settings => reader.Settings;
    public override string GetAttribute(int i) => reader.GetAttribute(i);
    public override string? GetAttribute(string name) => reader.GetAttribute(name);
    public override string? GetAttribute(string name, string? namespaceURI) => reader.GetAttribute(name, namespaceURI);
    public override string? LookupNamespace(string prefix) => reader.LookupNamespace(prefix);
    public override bool MoveToAttribute(string name) => reader.MoveToAttribute(name);
    public override bool MoveToAttribute(string name, string? ns) => reader.MoveToAttribute(name, ns);
    public override void MoveToAttribute(int i) => reader.MoveToAttribute(i);
    public override bool MoveToElement() => reader.MoveToElement();
    public override bool MoveToFirstAttribute() => reader.MoveToFirstAttribute();
    public override bool MoveToNextAttribute() => reader.MoveToNextAttribute();
    public override bool Read() => reader.Read();
    public override bool ReadAttributeValue() => reader.ReadAttributeValue();
    public override void ResolveEntity() => reader.ResolveEntity();
    public override void Close() => reader.Close();
    protected override void Dispose(bool disposing) { if (disposing) reader.Dispose(); base.Dispose(disposing); }
}
