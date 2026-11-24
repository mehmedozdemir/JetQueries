using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JetQueries.Core.Connections;
using JetQueries.Wpf.Services;
using JetQueries.Wpf.ViewModels;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>
/// Renders the real WPF windows (UiKit tokens and styles, real view models over the fake provider) in both
/// themes to PNG files under <c>screenshots/</c> next to the test assembly. The assertions only guard against a
/// blank or broken render; the images are for looking at.
/// </summary>
[Collection(WpfUi.Name)]
public sealed class ShellRenderTests
{
    [Fact]
    public async Task The_shell_and_dialogs_render_in_both_themes()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);

        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            App.LocalizeGrid();
            var results = new Dictionary<string, int>();

            await using var env = new TestEnvironment();
            var ui = new WpfUiDispatcher();
            var test = TestEnvironment.Definition("Kartlar — test", "Müşteri A", ConnectionEnvironment.Test);
            await env.Store.SaveAsync(test, CancellationToken.None);
            await env.Credentials.SetPasswordAsync(test.Id, "x", CancellationToken.None);
            await env.Store.SaveAsync(TestEnvironment.Definition("Kartlar — canlı", "Müşteri A", ConnectionEnvironment.Production) with { IsReadOnly = true }, CancellationToken.None);
            await env.Store.SaveAsync(TestEnvironment.Definition("Yerel geliştirme", string.Empty, ConnectionEnvironment.Development), CancellationToken.None);

            // Explorer opened down to the columns of a table.
            var connections = env.Connections();
            await connections.LoadAsync();
            var node = connections.Roots.SelectMany(static r => r.Children).OfType<ConnectionNodeViewModel>().Single(n => n.Definition.Id == test.Id);
            connections.Selected = node;
            await connections.ToggleConnectionCommand.ExecuteAsync(null);
            await TestEnvironment.LoadedAsync(node);
            var database = (DatabaseObjectNodeViewModel)node.Children[0];
            await TestEnvironment.ExpandAsync(database);
            var schema = (DatabaseObjectNodeViewModel)database.Children[0];
            await TestEnvironment.ExpandAsync(schema);
            var tables = (CategoryNodeViewModel)schema.Children[0];
            await TestEnvironment.ExpandAsync(tables);
            await TestEnvironment.ExpandAsync((DatabaseObjectNodeViewModel)tables.Children[0]);

            // A query with results, a second tab, and some history.
            var documents = new DocumentsViewModel(env.Manager, env.Metadata, env.History, env.Jobs, env.Dialogs, ui);
            var query = documents.Selected!;
            query.RefreshSessions();
            query.Text = "-- Bu yıl verilen kartlar\nSELECT c.Id, c.CardNo, c.Balance, c.IssuedAt\nFROM dbo.Cards c\nWHERE c.IssuedAt >= '2026-01-01' AND c.Status = @status\nORDER BY c.Id; /* ilk 100 */";
            await query.RunCommand.ExecuteAsync(null);
            documents.NewQueryCommand.Execute(null);
            documents.Selected = query;
            // Manual commit with a transaction open, as the toolbar shows it.
            await query.ToggleAutoCommitCommand.ExecuteAsync(null);
            env.Provider.TransactionOpen = true;
            await query.RefreshTransactionAsync();
            await env.History.AddAsync(TestEnvironment.HistoryEntry("UPDATE dbo.Cards SET Balance = 0 WHERE Id = 42"), CancellationToken.None);
            await env.History.AddAsync(
                TestEnvironment.HistoryEntry("SELECT * FROM dbo.Cardz") with { Outcome = Core.Queries.QueryOutcome.Failed, Error = "Invalid object name 'dbo.Cardz'.", RowCount = null },
                CancellationToken.None);
            var history = new HistoryViewModel(env.History, env.Jobs, env.Dialogs, ui, documents);
            await history.LoadAsync();
            await Idle();

            var shell = new MainWindowViewModel(new TestTheme(), ui, connections, documents, history);
            shell.ShowExtensions(4, 0);
            var window = new MainWindow(shell) { Width = 1280, Height = 760, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();

            var editor = new ConnectionEditorViewModel(null, [env.Provider], env.Store, env.Credentials, env.Manager, env.Jobs) { Name = "Kartlar — test" };
            editor.Fields[0].Value = "10.20.30.40,1433";
            editor.UserName = "app_reader";
            var dialog = new ConnectionDialog(editor) { Left = -20000, Top = 0, ShowActivated = false };
            dialog.Show();

            // The command palette lists the menu's commands and runs one as the menu would.
            var palette = window.ShowCommandPalette();
            var paletteModel = (CommandPaletteViewModel)palette.DataContext;
            Assert.Contains(paletteModel.Shown, static c => c.Title == "Sorgu geçmişi" && c.Group == "Araçlar" && c.Gesture == "Ctrl+Shift+H");
            Assert.DoesNotContain(paletteModel.Shown, static c => c.Title == "Komut paleti");
            paletteModel.SearchText = "gezgin";
            Assert.True(paletteModel.Run());
            await Idle();
            Assert.False(window.CurrentLayout().ExplorerVisible);
            palette.Close();

            // The explorer comes back as it was left.
            window.ApplyLayout(new ShellLayout { ExplorerVisible = true, ExplorerWidth = 320 });
            await Idle();
            Assert.Equal(320, window.CurrentLayout().ExplorerWidth, 0);
            window.ApplyLayout(new ShellLayout());
            await Idle();

            // The history has its own window (Tools, Query history), opened once and reused.
            var historyWindow = window.ShowHistory(shell);
            historyWindow.Left = -20000;
            Assert.Same(historyWindow, window.ShowHistory(shell));
            history.Selected = history.Entries[0];
            await Idle();
            Assert.Equal(history.Entries[0].Entry.Sql, historyWindow.PreviewText);

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                var suffix = theme == AppTheme.Dark ? "dark" : "light";
                await Idle();
                results[$"window-{suffix}"] = Capture(window, Path.Combine(directory, $"wpf-window-{suffix}.png"));
                results[$"history-{suffix}"] = Capture(historyWindow, Path.Combine(directory, $"wpf-history-{suffix}.png"));
                results[$"dialog-{suffix}"] = Capture(dialog, Path.Combine(directory, $"wpf-dialog-{suffix}.png"));
            }

            dialog.Close();
            historyWindow.Close();

            // Larger text from the settings, the explorer filtered, the editor zoomed to 120 %.
            ThemeManager.Apply(AppTheme.Dark, save: false);
            var appearance = new AppearanceService();
            appearance.Apply(new AppearanceSettings { UiFontSize = 15, EditorFontSize = 14, GridFontSize = 15 });
            connections.FilterText = "Cards";
            var sqlEditor = Find<Editor.SqlEditor>(window);
            sqlEditor.Zoom = 1.2;
            await Idle();
            Assert.True(query.CanChangeDatabase);
            Assert.Equal("Cards", query.SelectedDatabase);
            Assert.Equal(14 * 1.2, Find<ICSharpCode.AvalonEdit.TextEditor>(sqlEditor).FontSize, 3);
            Assert.Equal(15d, (double)Application.Current.Resources["FontSize.Body"]);
            results["window-large"] = Capture(window, Path.Combine(directory, "wpf-window-large-dark.png"));

            // The settings screen; closing it without saving puts the defaults back.
            var settings = new SettingsViewModel(
                new Core.Settings.JsonSettingsStore(new Core.Settings.SettingsStoreOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Core.Settings.JsonSettingsStore>.Instance),
                env.Jobs, appearance, new TestTheme(), ["Segoe UI", "Segoe UI Variable Text, Segoe UI", "Cascadia Mono, Consolas", "Consolas", "Arial"],
                new JetQueries.Provider.SqlServer.SqlServerProvider().LanguageService);
            var settingsWindow = new SettingsWindow(settings) { Left = -20000, Top = 0, ShowActivated = false };
            settingsWindow.Show();
            settings.FormatContentOnNewLine = false;
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                foreach (var section in settings.Sections)
                {
                    settings.SelectedSection = section;
                    await Idle();
                    var suffix = theme == AppTheme.Dark ? "dark" : "light";
                    results[$"settings-{section.Key}-{suffix}"] = Capture(settingsWindow, Path.Combine(directory, $"wpf-settings-{section.Key}-{suffix}.png"));
                }
            }

            // The formatting preview shows the options at work.
            Assert.StartsWith("-- Son 30", settings.FormatPreview, StringComparison.Ordinal);
            Assert.Contains("\nSELECT k.id,\n", settings.FormatPreview, StringComparison.Ordinal);

            settingsWindow.Close();

            // Go to object: "kart" over the open connection, then the first result revealed and selected in the tree.
            var goTo = new GoToObjectViewModel(connections, ui);
            var goToWindow = new GoToObjectWindow(goTo) { Left = -20000, Top = 0, ShowActivated = false };
            goToWindow.Show();
            await goTo.IndexAsync();
            goTo.SearchText = "card";
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                await Idle();
                results[$"goto-{theme}"] = Capture(goToWindow, Path.Combine(directory, $"wpf-goto-{(theme == AppTheme.Dark ? "dark" : "light")}.png"));
            }

            ThemeManager.Apply(AppTheme.Dark, save: false);
            var chosen = goTo.Results.First(static r => r.Name == "CardSummary").Node;
            goTo.Selected = goTo.Results.First(r => ReferenceEquals(r.Node, chosen));
            goTo.OpenCommand.Execute(null);
            await Idle();
            Assert.False(goToWindow.IsVisible);
            var explorer = Find<ConnectionsView>(window);
            Assert.True(explorer.ContainerOf(chosen)?.IsSelected);

            var saveChanges = new SaveChangesDialog("Sorgu 1") { Left = -20000, Top = 0, ShowActivated = false };
            saveChanges.Show();
            await Idle();
            results["save-changes"] = Capture(saveChanges, Path.Combine(directory, "wpf-save-changes-dark.png"));
            saveChanges.Close();
            Assert.Equal(15d, (double)Application.Current.Resources["FontSize.Body"]);
            appearance.Apply(new AppearanceSettings());
            connections.FilterText = string.Empty;
            window.Close();
            ThemeManager.Apply(AppTheme.Dark, save: false);
            return results;
        });

        Assert.Equal(22, colours.Count);
        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }

    internal static T Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is T found && !ReferenceEquals(node, root))
            {
                return found;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} under {root}.");
    }

    private static async Task Idle()
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
        }
    }

    private static int Capture(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            context.DrawRectangle((Brush)window.FindResource("Brush.Window"), null, bounds);
            context.DrawRectangle(new VisualBrush(content), null, bounds);
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        var pixels = new uint[bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels.Where(static (_, i) => i % 3 == 0).Distinct().Count();
    }

    private sealed class TestTheme : IThemeService
    {
        public event EventHandler? Changed;

        public bool IsDark => ThemeManager.Current == AppTheme.Dark;

        public void Toggle() => Changed?.Invoke(this, EventArgs.Empty);

        public void SetDark(bool dark) => Changed?.Invoke(this, EventArgs.Empty);
    }
}
