using WpfStudio.Database.Services;

namespace WpfStudio.Database.Tests;

public sealed class SqlBatchParserTests
{
    [Fact]
    public void SplitsGoWithCommentsAndRetainsSourcePositions()
    {
        const string sql = "SELECT 1;\r\nGO -- separator\r\nSELECT 2;\r\ngo /* comment */\r\nSELECT 3;";
        var batches = SqlBatchParser.Parse(sql, 20);
        Assert.Equal(3, batches.Count);
        Assert.Equal([20, 22, 24], batches.Select(batch => batch.StartLine));
        foreach (var batch in batches) Assert.Equal(batch.Text, sql.Substring(batch.StartOffset, batch.Text.Length));
    }

    [Theory]
    [InlineData("SELECT 'GO';\nGO\nSELECT 2", 2)]
    [InlineData("SELECT 'a\nGO\nb';\nGO\nSELECT 2", 2)]
    [InlineData("/* start\nGO\n/* nested */\n*/\nSELECT 1", 1)]
    [InlineData("SELECT [a\nGO\nb], \"GO\";", 1)]
    [InlineData("SELECT 'can''t GO', [a]]b], \"a\"\"b\";\nGO", 1)]
    [InlineData("SELECT '$(Allowed)'; -- :r ignored\nGO", 1)]
    [InlineData("SELECT 1;\n/* annotation */ GO\nSELECT 2", 2)]
    public void IgnoresSeparatorsAndDirectivesInsideQuotedOrCommentedText(string sql, int count) => Assert.Equal(count, SqlBatchParser.Parse(sql).Count);

    [Theory]
    [InlineData("GO 2")]
    [InlineData("GO -1")]
    [InlineData(":r file.sql")]
    [InlineData(":setvar name value")]
    [InlineData("!! whoami")]
    [InlineData("SELECT $(Value)")]
    public void RejectsUnsupportedSqlCmdWithCorrectLine(string directive)
    {
        var error = Assert.Throws<SqlScriptException>(() => SqlBatchParser.Parse("SELECT 1\n" + directive, 7));
        Assert.Equal(8, error.Line);
    }
}
