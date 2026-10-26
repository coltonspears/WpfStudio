namespace WpfStudio.Workspace.Tests;

public sealed class EditorDecorationsTests
{
    private const string Source = """
        using System.Collections.Generic;
        namespace App;
        public interface IClock { }
        public enum Mode { Fast }
        public class Order<TItem>
        {
            private int _count;
            public const int Limit = 5;
            public string Name { get; set; } = "";
            public void Add(TItem item, int quantity, bool urgent, string note) { var total = _count + quantity; }
            public void SetEnabled(bool enabled) { }
            public static int Max(int value1, int value2) => value1;
            public void Run(List<string> names, IClock clock, Mode mode)
            {
                var first = names[0];
                var copy = new List<string>();
                foreach (var name in names) { }
                Add(default!, 3, true, "rush");
                Add(default!, quantity: 1, urgent: false, note: "x");
                SetEnabled(true);
                Max(1, 2);
                Run(names, clock, Mode.Fast);
            }
        }
        """;

    [Fact]
    public async Task SemanticKindsCoverTypesMembersParametersAndLocals()
    {
        using var fixture = RoslynFixture.Create(Source);
        var spans = await EditorDecorations.ClassifyAsync(fixture.Document, CancellationToken.None);
        string KindAt(string text, int occurrence = 0)
        {
            var start = -1;
            for (var i = 0; i <= occurrence; i++) start = Source.IndexOf(text, start + 1, StringComparison.Ordinal);
            return Assert.Single(spans, span => span.Start == start && span.Length == text.Length).Kind;
        }
        Assert.Equal("Interface", KindAt("IClock"));
        Assert.Equal("Enum", KindAt("Mode"));
        Assert.Equal("Type", KindAt("Order"));
        Assert.Equal("TypeParameter", KindAt("TItem"));
        Assert.Equal("Field", KindAt("_count"));
        Assert.Equal("Constant", KindAt("Limit"));
        Assert.Equal("Property", KindAt("Name"));
        Assert.Equal("Method", KindAt("Add"));
        Assert.Equal("Parameter", KindAt("quantity", 1));
        Assert.Equal("Local", KindAt("total"));
        Assert.Equal("EnumMember", KindAt("Fast", 1));
        Assert.Equal("Namespace", KindAt("System"));
        Assert.Equal(spans.OrderBy(s => s.Start), spans);
    }

    [Fact]
    public async Task HintsNameLiteralArgumentsAndNonApparentVarTypes()
    {
        using var fixture = RoslynFixture.Create(Source);
        var hints = await EditorDecorations.GetHintsAsync(fixture.Document, CancellationToken.None);
        var texts = hints.Select(hint => hint.Text).ToArray();
        string At(int position) => Assert.Single(hints, hint => hint.Position == position).Text;

        var call = Source.IndexOf("Add(default!, 3, true, \"rush\")", StringComparison.Ordinal);
        Assert.Equal("quantity:", At(Source.IndexOf("3, true", call, StringComparison.Ordinal)));
        Assert.Equal("urgent:", At(Source.IndexOf("true, \"rush\"", call, StringComparison.Ordinal)));
        Assert.Equal("note:", At(Source.IndexOf("\"rush\"", call, StringComparison.Ordinal)));
        Assert.DoesNotContain(hints, hint => hint.Position == Source.IndexOf("default!", call, StringComparison.Ordinal)); // not a literal

        Assert.Equal(": string?", At(Source.IndexOf("first", StringComparison.Ordinal) + "first".Length)); // var locals are nullable
        Assert.StartsWith(": string", At(Source.IndexOf("var name", StringComparison.Ordinal) + "var name".Length));
        Assert.DoesNotContain(hints, hint => hint.Position == Source.IndexOf("copy", StringComparison.Ordinal) + 4); // new List<string>() is apparent
        Assert.DoesNotContain("enabled:", texts);  // SetEnabled(true) already says it
        Assert.DoesNotContain("value1:", texts);   // numbered names add nothing
        Assert.Equal(": int", At(Source.IndexOf("total", StringComparison.Ordinal) + "total".Length));
        Assert.Equal(6, hints.Count);
    }
}
