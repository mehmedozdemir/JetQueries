using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UiKit.Controls;

/// <summary>
/// Very fast single-line text drawing using cached glyph metrics and <see cref="GlyphRun"/>s
/// (no text layout / shaping per cell). Falls back to <see cref="FormattedText"/> for characters the
/// font cannot render (CJK, emoji, right-to-left scripts) so WPF font fallback still applies.
/// </summary>
internal sealed class GlyphTextRenderer
{
    private const char Ellipsis = '…';

    private readonly Typeface _typeface;
    private readonly GlyphTypeface _glyphTypeface;
    private readonly IDictionary<int, ushort> _cmap;
    private readonly IDictionary<ushort, double> _advances;
    private readonly ushort[] _glyphCache = new ushort[char.MaxValue + 1];
    private readonly float[] _widthCache = new float[char.MaxValue + 1]; // 0 = unknown, -1 = unsupported

    public GlyphTextRenderer(Typeface typeface, double size)
    {
        _typeface = typeface;
        Size = size;
        if (!typeface.TryGetGlyphTypeface(out var glyphTypeface))
        {
            var fallback = new Typeface(new FontFamily("Segoe UI"), typeface.Style, typeface.Weight, typeface.Stretch);
            if (!fallback.TryGetGlyphTypeface(out glyphTypeface))
                throw new InvalidOperationException("Yazı tipi yüklenemedi.");
            _typeface = fallback;
        }
        _glyphTypeface = glyphTypeface;
        _cmap = glyphTypeface.CharacterToGlyphMap;
        _advances = glyphTypeface.AdvanceWidths;

        Ascent = glyphTypeface.Baseline * size;
        LineHeight = glyphTypeface.Height * size;
        EllipsisWidth = TryGetWidth(Ellipsis, out double w) ? w : TryGetWidth('.', out double dot) ? dot * 3 : size;
    }

    public double Size { get; }

    public double Ascent { get; }

    public double LineHeight { get; }

    public double EllipsisWidth { get; }

    public Typeface Typeface => _typeface;

    private bool TryGetWidth(char c, out double width)
    {
        float cached = _widthCache[c];
        if (cached > 0)
        {
            width = cached;
            return true;
        }
        if (cached < 0)
        {
            width = 0;
            return false;
        }

        if (_cmap.TryGetValue(c, out ushort glyph) && !IsComplex(c))
        {
            _glyphCache[c] = glyph;
            width = _advances[glyph] * Size;
            _widthCache[c] = (float)Math.Max(width, 0.0001);
            return true;
        }

        _widthCache[c] = -1;
        width = 0;
        return false;
    }

    private static bool IsComplex(char c) =>
        char.IsSurrogate(c) || (c >= '֐' && c <= 'ࣿ') || (c >= 'יִ' && c <= 'ﻼ') || (c >= '̀' && c <= 'ͯ');

    private static char Normalize(char c) => c < ' ' ? ' ' : c;

    /// <summary>Width of the text in DIPs (with fallback measurement when needed).</summary>
    public double Measure(string text, double pixelsPerDip, int maxChars = 512)
    {
        double width = 0;
        int n = Math.Min(text.Length, maxChars);
        for (int i = 0; i < n; i++)
        {
            if (!TryGetWidth(Normalize(text[i]), out double w))
                return MeasureFallback(text.Length > maxChars ? text[..maxChars] : text, pixelsPerDip);
            width += w;
        }
        return width;
    }

    /// <summary>Returns true when the text needed truncation.</summary>
    public bool Draw(DrawingContext dc, string text, Brush brush, double x, double baselineY, double maxWidth,
        bool alignRight, double pixelsPerDip)
    {
        if (text.Length == 0 || maxWidth <= 2)
            return false;

        // First pass: how many characters fit, and is everything supported?
        double width = 0;
        int count = 0;
        bool truncated = false;
        int limit = text.Length;
        for (; count < limit; count++)
        {
            char c = Normalize(text[count]);
            if (!TryGetWidth(c, out double w))
                return DrawFallback(dc, text, brush, x, baselineY, maxWidth, alignRight, pixelsPerDip);
            if (width + w > maxWidth)
            {
                truncated = true;
                break;
            }
            width += w;
        }

        if (truncated)
        {
            // Remove characters until the ellipsis fits.
            while (count > 0 && width + EllipsisWidth > maxWidth)
            {
                count--;
                TryGetWidth(Normalize(text[count]), out double w);
                width -= w;
            }
            // Avoid trailing spaces before the ellipsis.
            while (count > 0 && text[count - 1] == ' ')
            {
                count--;
                TryGetWidth(' ', out double w);
                width -= w;
            }
        }

        int glyphCount = count + (truncated ? 1 : 0);
        if (glyphCount == 0)
            return truncated;

        var glyphs = new ushort[glyphCount];
        var advances = new double[glyphCount];
        for (int i = 0; i < count; i++)
        {
            char c = Normalize(text[i]);
            glyphs[i] = _glyphCache[c];
            advances[i] = _widthCache[c];
        }
        double total = width;
        if (truncated)
        {
            TryGetWidth(Ellipsis, out double ew);
            glyphs[count] = _glyphCache[Ellipsis];
            advances[count] = ew;
            total += ew;
        }

        double originX = alignRight ? x + maxWidth - total : x;
        originX = Math.Round(originX * pixelsPerDip) / pixelsPerDip;
        double originY = Math.Round(baselineY * pixelsPerDip) / pixelsPerDip;

        var run = new GlyphRun(_glyphTypeface, 0, false, Size, (float)pixelsPerDip, glyphs, new Point(originX, originY),
            advances, null, null, null, null, null, null);
        dc.DrawGlyphRun(brush, run);
        return truncated;
    }

    private bool DrawFallback(DrawingContext dc, string text, Brush brush, double x, double baselineY, double maxWidth,
        bool alignRight, double pixelsPerDip)
    {
        var formatted = CreateFormatted(text.Length > 1024 ? text[..1024] : text, brush, pixelsPerDip);
        formatted.MaxTextWidth = Math.Max(1, maxWidth);
        formatted.MaxLineCount = 1;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        if (alignRight)
            formatted.TextAlignment = TextAlignment.Right;
        dc.DrawText(formatted, new Point(x, baselineY - formatted.Baseline));
        return formatted.WidthIncludingTrailingWhitespace > maxWidth;
    }

    private double MeasureFallback(string text, double pixelsPerDip) =>
        CreateFormatted(text, Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace;

    private FormattedText CreateFormatted(string text, Brush brush, double pixelsPerDip)
    {
        var clean = string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = Normalize(source[i]);
        });
        return new FormattedText(clean, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, Size, brush, pixelsPerDip);
    }
}
