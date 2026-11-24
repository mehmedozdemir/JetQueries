using JetQueries.Sdk;

namespace JetQueries.Provider.PostgreSql;

/// <summary>
/// The parameters of user functions and procedures for signature help, from <c>pg_proc</c>: one signature per
/// overload, from the first schema of the search path that has the name (or the given schema).
/// </summary>
internal sealed partial class PostgreSqlMetadataProvider : IRoutineMetadataProvider
{
    /// <summary>Every overload's parameters and result; empty when no routine has the name.</summary>
    /// <remarks>
    /// The kind asked for is not used: <c>CALL p(</c> reads like any other call, and a name is either a function
    /// or a procedure in one schema. OUT parameters of functions are not passed in a call and are left out;
    /// a procedure's OUT parameters are passed (as NULL) and are kept.
    /// </remarks>
    public async Task<IReadOnlyList<SqlSignature>> GetSignaturesAsync(DatabaseObject routine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routine);

        // proallargtypes lists every argument (with OUT ones) and is null when all are IN; proargtypes then has them.
        const string Sql = """
            SELECT p.oid, n.nspname, p.proname, p.prokind::text,
                   CASE WHEN p.prokind = 'p' THEN NULL ELSE pg_get_function_result(p.oid) END,
                   a.ord::int, a.name, format_type(a.type, NULL), COALESCE(a.mode::text, 'i'),
                   a.ord IS NOT NULL AND pg_get_function_arg_default(p.oid, a.ord::int) IS NOT NULL
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            LEFT JOIN LATERAL unnest(COALESCE(p.proallargtypes, p.proargtypes::oid[]), p.proargnames, p.proargmodes)
                 WITH ORDINALITY a(type, name, mode, ord) ON a.type IS NOT NULL
            WHERE p.proname = @name AND p.prokind IN ('f', 'p', 'a', 'w')
              AND (n.nspname = @schema OR (@schema IS NULL AND n.nspname = ANY(current_schemas(false))))
            ORDER BY array_position(current_schemas(false), n.nspname::text), p.oid, a.ord
            """;
        var rows = await RowsAsync(Sql, [Text("schema", routine.Name.Schema), Text("name", routine.Name.Name)], cancellationToken).ConfigureAwait(false);
        if (rows is not [var head, ..])
        {
            return [];
        }

        var schema = (string)head[1]!;
        var signatures = new List<SqlSignature>();
        foreach (var overload in rows.Where(r => (string)r[1]! == schema).GroupBy(static r => (uint)r[0]!))
        {
            var first = overload.First();
            var procedure = (string?)first[3] == "p";
            var variadic = false;
            var parameters = new List<SqlParameterInfo>();
            foreach (var a in overload.Where(static a => a[5] is not null))
            {
                var mode = (string?)a[8];
                if (mode == "t" || (mode == "o" && !procedure))
                {
                    continue;
                }

                variadic |= mode == "v";
                var name = (string?)a[6] is { Length: > 0 } named ? named : "$" + (parameters.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameters.Add(new SqlParameterInfo(name, (string?)a[7], IsOptional: (bool)a[9]!)
                {
                    Direction = mode switch { "o" => "OUT", "b" => "IN OUT", _ => null },
                });
            }

            signatures.Add(new SqlSignature((string)first[2]!, parameters, (string?)first[4]) { IsVariadic = variadic });
        }

        return signatures;
    }
}
