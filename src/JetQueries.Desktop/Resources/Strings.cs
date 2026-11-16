using System.Globalization;
using System.Resources;

namespace JetQueries.Desktop.Resources;

/// <summary>
/// Typed access to the user-facing strings in <c>Strings.resx</c> (English) and <c>Strings.tr.resx</c> (Turkish).
/// The language follows <see cref="CultureInfo.CurrentUICulture"/>.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new(typeof(Strings));

    public static string ToggleTheme => Get(nameof(ToggleTheme));
    public static string ExplorerTitle => Get(nameof(ExplorerTitle));
    public static string ExplorerPlaceholder => Get(nameof(ExplorerPlaceholder));
    public static string DocumentsTitle => Get(nameof(DocumentsTitle));
    public static string DocumentsPlaceholder => Get(nameof(DocumentsPlaceholder));
    public static string ResultsTitle => Get(nameof(ResultsTitle));
    public static string ResultsPlaceholder => Get(nameof(ResultsPlaceholder));
    public static string StatusReady => Get(nameof(StatusReady));

    private static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
