using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JetQueries.Presentation.Resources;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A shortcut in the overview.</summary>
/// <param name="Group">Where it works (General, Editor, Results, Explorer).</param>
/// <param name="Keys">The keys, as printed on the keyboard.</param>
/// <param name="Description">What it does.</param>
public sealed record KeyboardShortcut(string Group, string Keys, string Description);

/// <summary>The keyboard shortcuts overview (Tools, Keyboard shortcuts, F1), searchable by keys or description.</summary>
public partial class KeyboardShortcutsViewModel : ObservableObject
{
    public KeyboardShortcutsViewModel()
    {
        All = Build();
        Filter();
    }

    /// <summary>Every shortcut, grouped in display order.</summary>
    public IReadOnlyList<KeyboardShortcut> All { get; }

    /// <summary>The shortcuts that match <see cref="SearchText"/>.</summary>
    public ObservableCollection<KeyboardShortcut> Shown { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => Filter();

    private void Filter()
    {
        var text = SearchText.Trim();
        Shown.Clear();
        foreach (var shortcut in All.Where(s => text.Length == 0 ||
                     s.Keys.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                     s.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                     s.Group.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
        {
            Shown.Add(shortcut);
        }
    }

    private static List<KeyboardShortcut> Build()
    {
        var general = Strings.ShortcutsGeneral;
        var editor = Strings.ShortcutsEditor;
        var results = Strings.ShortcutsResults;
        var explorer = Strings.ShortcutsExplorer;
        return
        [
            new(general, "Ctrl+N", Strings.QueryNew),
            new(general, "Ctrl+O", Strings.FileOpen),
            new(general, "Ctrl+S", Strings.FileSave),
            new(general, "Ctrl+Shift+S", Strings.MenuSaveAs),
            new(general, "Ctrl+Shift+N", Strings.MenuGoToObject),
            new(general, "Ctrl+Shift+H", Strings.MenuHistory),
            new(general, "F1", Strings.MenuShortcuts),
            new(editor, "F5", Strings.EditorRun),
            new(editor, "Ctrl+Enter", Strings.EditorRunStatement),
            new(editor, "Ctrl+Shift+Enter, Ctrl+\\", Strings.EditorRunInNewTab),
            new(editor, "Esc", Strings.QueryCancel),
            new(editor, "Ctrl+Space", Strings.EditorCompletion),
            new(editor, "Ctrl+Shift+Space", Strings.EditorSignatureHelp),
            new(editor, "Ctrl+Click, F12", Strings.EditorShowDetails),
            new(editor, "Ctrl+Shift+F", Strings.EditorFormat),
            new(editor, "Ctrl+/", Strings.EditorToggleComment),
            new(editor, "Ctrl+Shift+U", Strings.EditorUpperCase),
            new(editor, "Ctrl+U", Strings.EditorLowerCase),
            new(editor, "Tab", Strings.ShortcutSnippet),
            new(editor, "Alt+Drag", Strings.ShortcutBoxSelection),
            new(editor, "Ctrl+Wheel, Ctrl+0", Strings.ShortcutZoom),
            new(editor, "Ctrl+Z, Ctrl+Y", Strings.EditorUndo + " / " + Strings.EditorRedo),
            new(results, "Ctrl+C", Strings.ShortcutCopyCells),
            new(results, "Ctrl+Shift+C", Strings.ShortcutCopyWithHeaders),
            new(results, "Ctrl+A", Strings.EditorSelectAll),
            new(results, "Ctrl+Shift+L", Strings.ShortcutClearFilters),
            new(results, "Ctrl+Wheel, Ctrl+0", Strings.ShortcutZoom),
            new(explorer, "F4", Strings.ExplorerDetails),
            new(explorer, Strings.ShortcutDoubleClick, Strings.ShortcutConnect),
        ];
    }
}
