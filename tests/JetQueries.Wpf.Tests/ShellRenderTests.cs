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

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                var suffix = theme == AppTheme.Dark ? "dark" : "light";
                window.FindName("SideTabs");
                ((TabControl)window.FindName("SideTabs")).SelectedIndex = 0;
                await Idle();
                results[$"window-{suffix}"] = Capture(window, Path.Combine(directory, $"wpf-window-{suffix}.png"));
                ((TabControl)window.FindName("SideTabs")).SelectedIndex = 1;
                query.SelectedTab = query.Messages;
                await Idle();
                results[$"history-{suffix}"] = Capture(window, Path.Combine(directory, $"wpf-history-{suffix}.png"));
                query.SelectedTab = query.Tabs[0];
                results[$"dialog-{suffix}"] = Capture(dialog, Path.Combine(directory, $"wpf-dialog-{suffix}.png"));
            }

            dialog.Close();
            window.Close();
            ThemeManager.Apply(AppTheme.Dark, save: false);
            return results;
        });

        Assert.Equal(6, colours.Count);
        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
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
    }
}
