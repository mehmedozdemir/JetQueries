using System.Globalization;
using System.Resources;

namespace JetQueries.Provider.Oracle.Resources;

/// <summary>
/// The provider's messages that reach the user (English and Turkish, in the language of
/// <see cref="CultureInfo.CurrentUICulture"/>).
/// </summary>
internal static class Messages
{
    private static readonly ResourceManager Manager = new("JetQueries.Provider.Oracle.Resources.Messages", typeof(Messages).Assembly);

    /// <summary>This screen reads Oracle's dynamic performance (V$) and DBA_ views: the login needs SELECT_CATALOG_ROLE (or SELECT ANY DICTIONARY). …</summary>
    public static string NeedsCatalogRole(object? value) => string.Format(CultureInfo.CurrentCulture, Get(nameof(NeedsCatalogRole)), value);

    /// <summary>Oracle does not report index fragmentation without ANALYZE INDEX … VALIDATE STRUCTURE, which locks the table; B-tree indexes rarely need rebuilding. Use the Segment Advisor (DBMS_SPACE) to find space worth reclaiming.</summary>
    public static string NoFragmentation => Get(nameof(NoFragmentation));

    /// <summary>No schema named … in this database.</summary>
    public static string NoSuchSchema(object? value) => string.Format(CultureInfo.CurrentCulture, Get(nameof(NoSuchSchema)), value);

    private static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
