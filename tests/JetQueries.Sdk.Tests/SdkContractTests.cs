using System.Reflection;
using JetQueries.Sdk;

namespace JetQueries.Sdk.Tests;

/// <summary>Guards the properties of the SDK that extensions and the host rely on.</summary>
public sealed class SdkContractTests
{
    private static readonly Assembly Sdk = typeof(IDatabaseProvider).Assembly;

    [Fact]
    public void Sdk_depends_only_on_the_base_class_library()
    {
        var references = Sdk.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.All(references, name => Assert.StartsWith("System.", name, StringComparison.Ordinal));
    }

    [Fact]
    public void Async_contract_methods_take_a_cancellation_token_last()
    {
        var asyncMethods = Sdk.GetExportedTypes()
            .Where(t => t.IsInterface)
            .SelectMany(t => t.GetMethods())
            .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType) ||
                        (m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)))
            .ToArray();

        Assert.NotEmpty(asyncMethods);
        Assert.All(asyncMethods, m => Assert.Equal(typeof(CancellationToken), m.GetParameters()[^1].ParameterType));
    }

    [Fact]
    public void Credential_never_reveals_its_password()
    {
        var credential = new ConnectionCredential("scott", "tiger");

        Assert.DoesNotContain("tiger", credential.ToString(), StringComparison.Ordinal);
        Assert.Contains("scott", credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Object_names_compare_ordinally_and_print_only_present_parts()
    {
        Assert.Equal(new DatabaseObjectName("Sales", "dbo", "Orders"), new DatabaseObjectName("Sales", "dbo", "Orders"));
        Assert.NotEqual(new DatabaseObjectName(null, "dbo", "Orders"), new DatabaseObjectName(null, "dbo", "ORDERS"));
        Assert.Equal("Sales.dbo.Orders", new DatabaseObjectName("Sales", "dbo", "Orders").ToString());
        Assert.Equal("HR.EMPLOYEES", new DatabaseObjectName(null, "HR", "EMPLOYEES").ToString());
        Assert.Equal("t", new DatabaseObjectName(null, null, "t").ToString());
    }

    [Fact]
    public void All_object_kinds_covers_every_single_kind()
    {
        var singles = Enum.GetValues<DatabaseObjectKinds>()
            .Where(k => k is not (DatabaseObjectKinds.None or DatabaseObjectKinds.All));

        Assert.All(singles, kind => Assert.True(DatabaseObjectKinds.All.HasFlag(kind)));
        Assert.Equal(DatabaseObjectKinds.All, singles.Aggregate(DatabaseObjectKinds.None, (acc, k) => acc | k));
    }

    [Fact]
    public void Text_span_contains_its_end_so_a_caret_after_a_word_still_hits_it()
    {
        var span = new TextSpan(4, 6);

        Assert.Equal(10, span.End);
        Assert.True(span.Contains(4));
        Assert.True(span.Contains(10));
        Assert.False(span.Contains(3));
        Assert.False(span.Contains(11));
    }

    [Fact]
    public void Query_request_defaults_are_safe_for_streaming()
    {
        var request = new QueryRequest("select 1");

        Assert.Empty(request.Parameters);
        Assert.Null(request.MaxRows);
        Assert.Null(request.Timeout);
        Assert.InRange(request.BatchSize, 1, 1000);
    }
}
