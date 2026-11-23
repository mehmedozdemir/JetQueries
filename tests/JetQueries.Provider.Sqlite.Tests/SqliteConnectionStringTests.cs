using JetQueries.Sdk;

namespace JetQueries.Provider.Sqlite.Tests;

public sealed class SqliteConnectionStringTests
{
    private static ParsedConnectionString Parse(string text) => new SqliteProvider().ParseConnectionString(text);

    [Theory]
    [InlineData(@"Data Source=C:\data\app.db;Mode=ReadOnly;Foreign Keys=False;Cache=Shared", "readOnly")]
    [InlineData(@"Data Source=""C:\data\app.db"";Read Only=True;Foreign Keys=False;Version=3", "readOnly")]
    [InlineData(@"Filename=C:\data\app.db;Mode=ReadWriteCreate;Foreign Keys=False", "create")]
    public void A_connection_string_is_read(string text, string mode)
    {
        var parsed = Parse(text);
        Assert.Equal(@"C:\data\app.db", parsed.Settings["file"]);
        Assert.Equal(mode, parsed.Settings["mode"]);
        Assert.Equal("false", parsed.Settings["foreignKeys"]);
        Assert.Empty(parsed.Ignored);
        Assert.Null(parsed.UserName);
    }

    [Theory]
    [InlineData(@"jdbc:sqlite:C:\data\app.db")]
    [InlineData(@"C:\data\app.db")]
    [InlineData(@"""C:\data\app.db""")]
    [InlineData("file:///C:/data/app.db")]
    public void A_path_is_the_file(string text)
    {
        Assert.Equal(@"C:\data\app.db", Parse(text).Settings["file"]);
    }

    [Fact]
    public void A_password_is_listed_as_not_taken_and_never_returned()
    {
        var parsed = Parse("Data Source=a.db;Password=pw");
        Assert.Equal(["password"], parsed.Ignored.Select(static k => k.ToLowerInvariant()));
        Assert.Null(parsed.Password);
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=a.db;Mode=Memory")]
    [InlineData("Mode=ReadOnly")]
    public void Memory_databases_and_missing_files_are_refused(string text)
    {
        Assert.Throws<FormatException>(() => Parse(text));
    }
}
