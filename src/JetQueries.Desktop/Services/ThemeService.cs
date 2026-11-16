using Avalonia;
using Avalonia.Styling;

namespace JetQueries.Desktop.Services;

internal sealed class ThemeService : IThemeService
{
    public bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public void Toggle()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = IsDark ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }
}
