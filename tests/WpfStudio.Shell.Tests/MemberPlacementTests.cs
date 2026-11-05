using WpfStudio.App.Services;

namespace WpfStudio.Shell.Tests;

public sealed class MemberPlacementTests
{
    private const string Model = "using System;\n\nnamespace Demo;\n\npublic partial class CustomerViewModel : ObservableObject\n{\n    private int _count;\n\n    public void Load()\n    {\n        Run();\n    }\n\n    public string Name { get; set; } = \"\";\n}\n";

    /// <summary>The text with MEMBER written where a member would go for a caret at the start of <paramref name="at"/>.</summary>
    private static string Place(string text, string at)
    {
        var place = MemberPlacement.Find(text, text.IndexOf(at, StringComparison.Ordinal), "    ", "\n");
        return MemberPlacement.Apply(text, place, "MEMBER", "\n");
    }

    [Fact]
    public void AMemberGoesAfterTheMemberTheCaretIsInNotIntoIt()
    {
        const string afterLoad = "        Run();\n    }\n\n    MEMBER\n\n    public string Name";
        Assert.Contains(afterLoad, Place(Model, "Run();"));
        Assert.Contains(afterLoad, Place(Model, "public void Load()"));
        Assert.Contains(afterLoad, Place(Model, "    {\n        Run"));
        Assert.Contains("    private int _count;\n\n    MEMBER\n\n    public void Load()", Place(Model, "private int _count;"));
        Assert.Contains("= \"\";\n\n    MEMBER\n}", Place(Model, "public string Name"));
    }

    [Fact]
    public void ABlankLineOrTheTypesOpeningBraceTakesTheMemberWhereItIs()
    {
        Assert.Contains("    private int _count;\n    MEMBER\n    public void Load()", Place(Model, "\n    public void Load()"));
        Assert.Contains("ObservableObject\n{\n    MEMBER\n\n    private int _count;", Place(Model, "{\n    private"));
        Assert.Equal("class C\n{\n    MEMBER\n}", Place("class C\n{\n}", "{"));
        // A blank line inside a method is still inside the method.
        const string blankInBody = "class C\n{\n    void M()\n    {\n\n        Run();\n    }\n}";
        Assert.Equal("class C\n{\n    void M()\n    {\n\n        Run();\n    }\n\n    MEMBER\n}", Place(blankInBody, "\n        Run"));
    }

    [Fact]
    public void TheTypesClosingBraceTakesTheMemberAboveItNotAfterTheType()
    {
        string At(string text, int caret) => MemberPlacement.Apply(text, MemberPlacement.Find(text, caret, "    ", "\n"), "MEMBER", "\n");
        // With the caret before the brace and after it: either way the member is the type's last.
        Assert.EndsWith("public string Name { get; set; } = \"\";\n\n    MEMBER\n}\n", At(Model, Model.LastIndexOf('}')));
        Assert.EndsWith("public string Name { get; set; } = \"\";\n\n    MEMBER\n}\n", At(Model, Model.LastIndexOf('}') + 1));
        // Nothing above it to be set off from.
        Assert.Equal("class C\n{\n    MEMBER\n}", At("class C\n{\n}", "class C\n{\n}".Length));
        Assert.Equal("class C\n{\n    int a;\n\n    MEMBER\n}", At("class C\n{\n    int a;\n\n}", "class C\n{\n    int a;\n\n}".Length));
        // An indented type, and a nested one: the member lands in the type whose brace it is.
        const string nested = "namespace N\n{\n    class Outer\n    {\n        class Inner\n        {\n            int a;\n        }\n    }\n}";
        Assert.Contains("            int a;\n\n            MEMBER\n        }\n    }", At(nested, nested.IndexOf("        }\n    }", StringComparison.Ordinal) + 8));
        Assert.Contains("        }\n\n        MEMBER\n    }\n}", At(nested, nested.IndexOf("    }\n}", StringComparison.Ordinal) + 4));
    }

    [Fact]
    public void AGenericConstraintDoesNotMakeAMethodLookLikeAType()
    {
        const string text = "class C\n{\n    void M<T>() where T : class\n    {\n        Run();\n    }\n}";
        Assert.Equal("class C\n{\n    void M<T>() where T : class\n    {\n        Run();\n    }\n\n    MEMBER\n}", Place(text, "Run();"));
    }

    [Fact]
    public void AMembersLinesTakeThePlacesIndentationAndLineEnding()
    {
        const string text = "class C\r\n{\r\n\tint a;\r\n}";
        var place = MemberPlacement.Find(text, text.IndexOf("int a", StringComparison.Ordinal), "\t", "\r\n");
        Assert.Equal("class C\r\n{\r\n\tint a;\r\n\r\n\t[Attribute]\r\n\tvoid M()\r\n\t{\r\n\t}\r\n}", MemberPlacement.Apply(text, place, "[Attribute]\nvoid M()\n{\n}\n", "\r\n"));
    }

    [Fact]
    public void AUsingIsAddedOnlyWhenTheNamespaceIsNotAlreadyInScope()
    {
        const string input = "CommunityToolkit.Mvvm.Input";
        Assert.Equal(("using System;\n".Length, "using CommunityToolkit.Mvvm.Input;\n"), MemberPlacement.UsingInsertion("using System;\n\nclass C { }", input, "\n"));
        Assert.Equal((0, "using CommunityToolkit.Mvvm.Input;\n\n"), MemberPlacement.UsingInsertion("class C { void M() { using var s = Open(); } }", input, "\n"));
        Assert.Null(MemberPlacement.UsingInsertion("using CommunityToolkit.Mvvm.Input;\nclass C { }", input, "\n"));
        Assert.Null(MemberPlacement.UsingInsertion("global using CommunityToolkit.Mvvm.Input;\nclass C { }", input, "\n"));
        // The attribute is already used by its short name, so the namespace reaches this file some other way.
        Assert.Null(MemberPlacement.UsingInsertion("class C { [RelayCommand] void Save() { } }", input, "\n"));
    }
}
