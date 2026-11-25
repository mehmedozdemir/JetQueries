using System.Globalization;
using System.Resources;

namespace JetQueries.Provider.SqlServer.Resources;

/// <summary>
/// The provider's messages that reach the user (English and Turkish, in the language of
/// <see cref="CultureInfo.CurrentUICulture"/>).
/// </summary>
internal static class Messages
{
    private static readonly ResourceManager Manager = new("JetQueries.Provider.SqlServer.Resources.Messages", typeof(Messages).Assembly);

    /// <summary>The login lacks VIEW SERVER STATE: only its own sessions are shown.</summary>
    public static string ActivityLimited => Get(nameof(ActivityLimited));

    /// <summary>No database named … on this server.</summary>
    public static string NoSuchDatabase(object? value) => string.Format(CultureInfo.CurrentCulture, Get(nameof(NoSuchDatabase)), value);

    private static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
