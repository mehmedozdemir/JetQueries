using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Desktop.Services;

namespace JetQueries.Desktop.ViewModels;

/// <summary>View model of the shell window.</summary>
public partial class MainWindowViewModel(IThemeService themeService) : ObservableObject
{
    [RelayCommand]
    private void ToggleTheme() => themeService.Toggle();
}
