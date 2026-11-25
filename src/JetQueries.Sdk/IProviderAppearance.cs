namespace JetQueries.Sdk;

/// <summary>
/// Optional feature of an <see cref="IDatabaseProvider"/>: how the host shows the database type (in the
/// connection editor's type list and in the explorer).
/// </summary>
/// <remarks>Preview: part of SDK contract version 0.</remarks>
public interface IProviderAppearance
{
    /// <summary>
    /// The mark of the database type: SVG path data (<c>d</c> attribute syntax) on a 24 × 24 canvas, drawn in
    /// <see cref="AccentColor"/> on a light tile. The product's own logo is best: it tells types apart at a glance.
    /// </summary>
    string IconPathData { get; }

    /// <summary>The type's colour as <c>#RRGGBB</c>.</summary>
    string AccentColor { get; }

    /// <summary>
    /// One short English line under the name (for example the products it covers); the host may replace it with a
    /// translation it has for the provider's id.
    /// </summary>
    string Description { get; }
}
