using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A command in the palette.</summary>
/// <param name="Title">What it does, as the menu names it.</param>
/// <param name="Group">Where it lives (the menu's name).</param>
/// <param name="Gesture">Its shortcut, if it has one.</param>
/// <param name="Execute">Runs it.</param>
/// <param name="CanExecute">Whether it can run now.</param>
public sealed record PaletteCommand(string Title, string Group, string? Gesture, Action Execute, Func<bool> CanExecute);

/// <summary>
/// The command palette (Ctrl+Shift+P): every menu command by name, filtered as one types (each word must occur
/// in the name or the menu), the available ones first.
/// </summary>
public partial class CommandPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<PaletteCommand> _all;

    public CommandPaletteViewModel(IEnumerable<PaletteCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _all = [.. commands];
        Filter();
    }

    /// <summary>The commands that match <see cref="SearchText"/>.</summary>
    public ObservableCollection<PaletteCommand> Shown { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PaletteCommand? Selected { get; set; }

    partial void OnSearchTextChanged(string value) => Filter();

    /// <summary>Runs the selected command if it can run; true when it ran (the palette then closes).</summary>
    public bool Run()
    {
        if (Selected is not { } command || !command.CanExecute())
        {
            return false;
        }

        command.Execute();
        return true;
    }

    /// <summary>Moves the selection by <paramref name="step"/> rows, staying in the list.</summary>
    public void Move(int step)
    {
        if (Shown.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : Shown.IndexOf(Selected);
        Selected = Shown[Math.Clamp(index + step, 0, Shown.Count - 1)];
    }

    private void Filter()
    {
        var words = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        bool Has(string text, string word) => compare.IndexOf(text, word, CompareOptions.IgnoreCase) >= 0;
        Shown.Clear();
        foreach (var command in _all
                     .Where(c => words.All(w => Has(c.Title, w) || Has(c.Group, w) || (c.Gesture is { } g && Has(g, w))))
                     .OrderBy(static c => c.CanExecute() ? 0 : 1))
        {
            Shown.Add(command);
        }

        Selected = Shown.FirstOrDefault();
    }
}
