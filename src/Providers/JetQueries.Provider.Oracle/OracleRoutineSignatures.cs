using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle;

/// <summary>
/// The parameters of stored procedures and functions for signature help, from ALL_PROCEDURES and
/// ALL_ARGUMENTS with bind variables. A package member is found by "package.member" in the name, or by a
/// "schema" part that turns out to be a package of the current schema (<c>pkg.member(</c> in the editor).
/// </summary>
internal sealed partial class OracleMetadataProvider : IRoutineMetadataProvider
{
    /// <summary>One signature per standalone routine or per package overload; empty when none is found.</summary>
    public async Task<IReadOnlyList<SqlSignature>> GetSignaturesAsync(DatabaseObject routine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routine);
        var name = routine.Name;

        // "PKG.MEMBER" in the name: a member of a package in the given (or current) schema. Otherwise a
        // standalone routine, or — when a schema part was written — a member of a package of that name.
        string? owner = null, standalone = null, packageOwner = null, package = null, member = name.Name;
        var dot = name.Name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            packageOwner = name.Schema;
            package = name.Name[..dot];
            member = name.Name[(dot + 1)..];
        }
        else
        {
            owner = name.Schema;
            standalone = name.Name;
            package = name.Schema;
        }

        // Position 0 is a function's return value; a routine without parameters has no argument rows here
        // (the left join keeps the routine). Every number is converted with TO_CHAR for RowsAsync.
        const string Sql = """
            SELECT p.object_type, p.object_name, p.procedure_name, TO_CHAR(p.subprogram_id), TO_CHAR(a.position), a.argument_name,
                   a.data_type, a.type_owner, a.type_name, a.type_subname, a.in_out, a.defaulted
            FROM all_procedures p
            LEFT JOIN all_arguments a
              ON a.object_id = p.object_id AND a.subprogram_id = p.subprogram_id AND a.data_level = 0 AND a.data_type IS NOT NULL
             AND (a.argument_name IS NOT NULL OR a.position = 0)
            WHERE (:standalone IS NOT NULL AND p.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND p.object_name = :standalone
                   AND p.procedure_name IS NULL AND p.object_type IN ('PROCEDURE', 'FUNCTION'))
               OR (:package IS NOT NULL AND p.object_type = 'PACKAGE' AND p.owner = NVL(:package_owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))
                   AND p.object_name = :package AND p.procedure_name = :member)
            ORDER BY CASE p.object_type WHEN 'PACKAGE' THEN 1 ELSE 0 END, p.subprogram_id, a.position
            """;
        var rows = await RowsAsync(
            Sql,
            [("standalone", standalone), ("owner", owner), ("package", package), ("package_owner", packageOwner), ("member", member)],
            cancellationToken).ConfigureAwait(false);

        // A standalone routine wins over a package member of the same spelling.
        if (rows.Any(static r => r[0] != "PACKAGE"))
        {
            rows = [.. rows.Where(static r => r[0] != "PACKAGE")];
        }

        var signatures = new List<SqlSignature>();
        foreach (var overload in rows.GroupBy(static r => (r[1], r[3])))
        {
            var first = overload.First();
            var isPackage = first[0] == "PACKAGE";
            string? returns = null;
            var parameters = new List<SqlParameterInfo>();
            foreach (var a in overload.Where(static a => a[4] is not null))
            {
                var dataType = a[8] is { } typeName ? (a[7] is { } typeOwner ? typeOwner + "." : string.Empty) + typeName + (a[9] is { } sub ? "." + sub : string.Empty) : a[6];
                if (a[4] == "0")
                {
                    returns = dataType;
                    continue;
                }

                parameters.Add(new SqlParameterInfo(a[5]!, dataType, IsOptional: a[11] == "Y")
                {
                    Direction = a[10] switch { "OUT" => "OUT", "IN/OUT" => "IN OUT", _ => null },
                });
            }

            signatures.Add(new SqlSignature(isPackage ? first[1] + "." + first[2] : first[1]!, parameters, returns));
        }

        return signatures;
    }
}
