namespace JetQueries.Desktop.Services;

/// <summary>Switches the application between the light and dark token sets.</summary>
public interface IThemeService
{
    /// <summary>True when the dark theme is currently in effect.</summary>
    bool IsDark { get; }

    /// <summary>Switches to the other theme. Must be called on the UI thread.</summary>
    void Toggle();
}
