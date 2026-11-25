using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>How a database type is drawn: its mark (SVG path data on a 24 × 24 canvas) in its colour, on a light tile.</summary>
/// <param name="PathData">The mark.</param>
/// <param name="Color">The colour, <c>#RRGGBB</c>.</param>
public sealed record ProviderMark(string PathData, string Color)
{
    /// <summary>A database cylinder in grey, for a provider that does not describe its look.</summary>
    public static ProviderMark Default { get; } = new(
        "M4 6c0-1.7 3.6-3 8-3s8 1.3 8 3v12c0 1.7-3.6 3-8 3s-8-1.3-8-3z M12 7.6c-3.3 0-5.8-.8-6.4-1.6.6-.8 3.1-1.6 6.4-1.6s5.8.8 6.4 1.6c-.6.8-3.1 1.6-6.4 1.6z",
        "#6B7280");

    /// <summary>The mark of <paramref name="provider"/>.</summary>
    public static ProviderMark For(IDatabaseProvider? provider) =>
        provider is IProviderAppearance { IconPathData.Length: > 0 } appearance && IsColor(appearance.AccentColor)
            ? new ProviderMark(appearance.IconPathData, appearance.AccentColor)
            : Default;

    /// <summary>The one-line description of <paramref name="provider"/>: the host's translation, else the provider's own.</summary>
    public static string DescriptionOf(IDatabaseProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Strings.Find("ProviderDescription_" + provider.Id) ?? (provider as IProviderAppearance)?.Description ?? string.Empty;
    }

    // An extension's colour is used only when it is exactly #RRGGBB.
    private static bool IsColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && !value.AsSpan(1).ContainsAnyExcept(HexDigits);

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");
}
