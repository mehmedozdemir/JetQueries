using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Tests;

/// <summary>The explorer's DROP and ALTER scripts for SQL Server.</summary>
public sealed class SqlServerScriptingTests
{
    private static readonly SqlServerProvider Provider = new();

    private static DatabaseObject Object(DatabaseObjectKinds kind, string name = "Kart]x") => new(new DatabaseObjectName("Db", "dbo", name), kind);

    [Theory]
    [InlineData(DatabaseObjectKinds.Table, "DROP TABLE [dbo].[Kart]]x];\nGO\n")]
    [InlineData(DatabaseObjectKinds.View, "DROP VIEW [dbo].[Kart]]x];\nGO\n")]
    [InlineData(DatabaseObjectKinds.Procedure, "DROP PROCEDURE [dbo].[Kart]]x];\nGO\n")]
    [InlineData(DatabaseObjectKinds.Sequence, "DROP SEQUENCE [dbo].[Kart]]x];\nGO\n")]
    public void Drop_quotes_the_schema_qualified_name(DatabaseObjectKinds kind, string expected)
    {
        Assert.Equal(expected, Provider.ScriptDrop(Object(kind)));
    }

    [Theory]
    [InlineData(DatabaseObjectKinds.Database)]
    [InlineData(DatabaseObjectKinds.Schema)]
    [InlineData(DatabaseObjectKinds.Package)]
    public void Kinds_without_a_drop_give_none(DatabaseObjectKinds kind)
    {
        Assert.Null(Provider.ScriptDrop(Object(kind)));
    }

    [Theory]
    [InlineData("CREATE VIEW dbo.v AS SELECT 1 AS n", "ALTER VIEW dbo.v AS SELECT 1 AS n")]
    [InlineData("-- header\n/* note */\ncreate   procedure dbo.p AS SELECT 1", "-- header\n/* note */\nALTER procedure dbo.p AS SELECT 1")]
    [InlineData("CREATE OR ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END", "ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END")]
    public void Alter_replaces_the_leading_create(string definition, string expected)
    {
        Assert.Equal(expected, Provider.ScriptAlter(Object(DatabaseObjectKinds.View), definition));
    }

    [Fact]
    public void Tables_and_texts_without_create_have_no_alter()
    {
        Assert.Null(Provider.ScriptAlter(Object(DatabaseObjectKinds.Table), "CREATE TABLE t (a int)"));
        Assert.Null(Provider.ScriptAlter(Object(DatabaseObjectKinds.View), "SELECT 1"));
    }
}
