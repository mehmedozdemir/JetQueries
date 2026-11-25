using System.Globalization;
using System.Resources;

namespace JetQueries.Provider.PostgreSql.Resources;

/// <summary>
/// The provider's messages that reach the user (English and Turkish, in the language of
/// <see cref="CultureInfo.CurrentUICulture"/>).
/// </summary>
internal static class Messages
{
    private static readonly ResourceManager Manager = new("JetQueries.Provider.PostgreSql.Resources.Messages", typeof(Messages).Assembly);

    /// <summary>The role lacks pg_read_all_stats: the statements of other users' sessions are hidden.</summary>
    public static string ActivityLimited => Get(nameof(ActivityLimited));

    /// <summary>PostgreSQL did not end process … (it may have ended already, or the role may not end it).</summary>
    public static string KillFailed(object? value) => string.Format(CultureInfo.CurrentCulture, Get(nameof(KillFailed)), value);

    /// <summary>The pg_stat_statements extension is not installed in this database: add it to shared_preload_libraries, restart PostgreSQL and run CREATE EXTENSION pg_stat_statements;</summary>
    public static string NoStatStatements => Get(nameof(NoStatStatements));

    /// <summary>Measuring index fragmentation needs the pgstattuple extension in this database: CREATE EXTENSION pgstattuple;</summary>
    public static string NoPgStatTuple => Get(nameof(NoPgStatTuple));

    private static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
