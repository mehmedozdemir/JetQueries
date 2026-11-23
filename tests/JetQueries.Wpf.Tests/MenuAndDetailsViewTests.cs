using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JetQueries.Sdk;
using JetQueries.Wpf.Views;
using UiKit.Controls;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>Submenus (Script, Copy as), the data types under the grid's headers, and the object details window.</summary>
[Collection(WpfUi.Name)]
public sealed class MenuAndDetailsViewTests
{
    [Fact]
    public async Task A_submenu_opens_and_its_items_can_be_clicked()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var host = new Window { Width = 300, Height = 200, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            host.Show();
            var clicked = false;
            var child = new MenuItem { Header = "CSV" };
            child.Click += (_, _) => clicked = true;
            var parent = new MenuItem { Header = "Copy as", Items = { child } };
            var menu = new ContextMenu { PlacementTarget = host, Items = { new MenuItem { Header = "Copy" }, parent } };
            menu.IsOpen = true;
            await EditorActionsTests.Idle();

            // UiKit's MenuItem had no submenu popup: the child was never shown.
            parent.IsSubmenuOpen = true;
            await EditorActionsTests.Idle();
            Assert.True(child.IsVisible);
            child.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(clicked);

            menu.IsOpen = false;
            host.Close();
            return 0;
        });
    }

    [Fact]
    public async Task The_explorer_menu_binds_its_script_items_to_the_explorer()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            await using var env = new TestEnvironment();
            var (connections, node) = await TestEnvironment.ConnectAsync(env);
            var view = new ConnectionsView { DataContext = connections };
            var host = new Window { Content = view, Width = 320, Height = 600, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            host.Show();

            // Cards > dbo > Tables > Cards
            await TestEnvironment.ExpandAsync(node);
            var database = (DatabaseObjectNodeViewModel)node.Children.First(static c => c.Name == "Cards");
            await TestEnvironment.ExpandAsync(database);
            var schema = (DatabaseObjectNodeViewModel)database.Children.First(static c => c.Name == "dbo");
            await TestEnvironment.ExpandAsync(schema);
            var tables = (LazyNodeViewModel)schema.Children.First();
            await TestEnvironment.ExpandAsync(tables);
            var table = (DatabaseObjectNodeViewModel)tables.Children.First(static c => c.Name == "Cards");
            await EditorActionsTests.Idle();

            var item = view.ContainerOf(table)!;
            item.IsSelected = true;
            await EditorActionsTests.Idle();
            var menu = item.ContextMenu!;
            menu.PlacementTarget = item;
            menu.IsOpen = true;
            await EditorActionsTests.Idle();

            Assert.Same(connections, menu.DataContext);
            var script = menu.Items.OfType<MenuItem>().Single(static m => (string)m.Header == Presentation.Resources.Strings.ExplorerScript);
            script.IsSubmenuOpen = true;
            await EditorActionsTests.Idle();
            var scripts = script.Items.OfType<MenuItem>().Where(static m => m.IsVisible).ToList();
            Assert.Equal(["CREATE (tanım)", "DROP", "SELECT", "INSERT", "UPDATE", "DELETE"], scripts.Select(static m => (string)m.Header));
            Assert.All(scripts, static m => Assert.NotNull(m.Command));
            Assert.Same(table, scripts[0].CommandParameter);

            // Details asks for this table.
            ObjectDetailsRequest? asked = null;
            connections.DetailsRequested += (_, request) => asked = request;
            var details = menu.Items.OfType<MenuItem>().Single(static m => (string)m.Header == Presentation.Resources.Strings.ExplorerDetails);
            details.Command.Execute(details.CommandParameter);
            Assert.Equal(table.Target, asked?.Target);

            menu.IsOpen = false;
            host.Close();
            return 0;
        });
    }

    [Fact]
    public async Task Column_headers_show_the_data_type_and_the_details_window_renders()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);

        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            var results = new Dictionary<string, int>();

            // The data type is the header's second line.
            var set = new QueryResultSet(
                "Sonuç 1", [new ResultColumn("Id", "int", typeof(int)), new ResultColumn("Ad", "nvarchar(50)", typeof(string))], null, null, new ImmediateDispatcher());
            set.AddRows([[1, "Kart"]]);
            var source = ResultGridSource.Snapshot(set);
            Assert.Equal("nvarchar(50)", ((IGridColumnCaptions)source).GetColumnCaption(1));
            var grid = new VirtualGrid { Source = source };
            var gridHost = new Window { Content = grid, Width = 400, Height = 200, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            gridHost.Show();
            await EditorActionsTests.Idle();
            results["grid"] = ResultGridViewTests.Capture(gridHost, Path.Combine(directory, "wpf-grid-header-types.png"));
            gridHost.Close();

            await using var env = new TestEnvironment();
            var orders = new DatabaseObject(new DatabaseObjectName("Cards", "dbo", "Orders"), DatabaseObjectKinds.Table);
            env.Provider.Details = target => new ObjectDetails(
                target,
                [new(ObjectDetailKeys.ObjectType, "Type", "USER TABLE"), new(ObjectDetailKeys.Schema, "Schema", "dbo"), new(ObjectDetailKeys.Rows, "Rows", "1,024")],
                [
                    new(ObjectDetailKeys.Columns, "Columns",
                        [new(ObjectDetailKeys.Position, "#"), new(ObjectDetailKeys.Name, "Name"), new(ObjectDetailKeys.DataType, "Data type"), new(ObjectDetailKeys.Nullable, "Nullable")],
                        [new(["1", "Id", "bigint", null]), new(["2", "CardNo", "varchar(19)", null]), new(["3", "Balance", "decimal(12,2)", "✓"])]),
                    new(ObjectDetailKeys.References, "References", [new(ObjectDetailKeys.Name, "Name"), new(ObjectDetailKeys.Table, "Table")],
                        [new(["FK_Orders_Cards", "dbo.Orders"]) { Target = orders }]),
                    new(ObjectDetailKeys.Triggers, "Triggers", [new(ObjectDetailKeys.Name, "Name"), new(ObjectDetailKeys.Events, "Events")],
                        [new(["trg_Cards_Audit", "INSERT, UPDATE"]) { Source = "CREATE TRIGGER trg_Cards_Audit ON dbo.Cards AFTER INSERT, UPDATE AS SET NOCOUNT ON;" }]),
                ]);
            var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
            var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
            var details = new ObjectDetailsViewModel(session, new DatabaseObject(new DatabaseObjectName("Cards", "dbo", "Cards"), DatabaseObjectKinds.Table), env.Metadata, env.Jobs);
            var window = new ObjectDetailsWindow(details) { Left = -20000, Top = 0, ShowActivated = false, Width = 900, Height = 520 };
            window.Show();
            await details.LoadAsync();
            await EditorActionsTests.Idle();
            Assert.Equal(["Kolonlar (3)", "Referanslar (1)", "Tetikleyiciler (1)"], window.SectionTitles);

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                await EditorActionsTests.Idle();
                results["details-" + theme] = ResultGridViewTests.Capture(window, Path.Combine(directory, $"wpf-details-{(theme == AppTheme.Dark ? "dark" : "light")}.png"));
            }

            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();
            return results;
        });

        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }

    [Fact]
    public async Task A_production_tab_is_marked_and_the_explorer_actions_fit_the_panel()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);

        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            var results = new Dictionary<string, int>();
            await using var env = new TestEnvironment();
            var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar — canlı", environment: Core.Connections.ConnectionEnvironment.Production));
            var query = env.Query();
            query.RefreshSessions();
            query.SelectSession(definition.Id);
            query.Text = "UPDATE dbo.Cards SET Balance = 0 WHERE Id = 42";
            var view = new QueryDocumentView { DataContext = query };
            var window = new Window { Content = view, Width = 900, Height = 420, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await EditorActionsTests.Idle();

            Assert.True(((FrameworkElement)view.FindName("EnvironmentStrip")).IsVisible);
            Assert.Equal("Kartlar — canlı", ((TextBlock)view.FindName("ConnectionName")).Text);
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                await EditorActionsTests.Idle();
                results["production-" + theme] = ResultGridViewTests.Capture(window, Path.Combine(directory, $"wpf-production-tab-{(theme == AppTheme.Dark ? "dark" : "light")}.png"));
            }

            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();

            // The explorer's actions stay inside the panel at its default width.
            var (connections, _) = await TestEnvironment.ConnectAsync(env);
            var explorer = new ConnectionsView { DataContext = connections };
            var panel = new Window { Content = explorer, Width = 280, Height = 300, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            panel.Show();
            await EditorActionsTests.Idle();
            var actions = (Panel)explorer.FindName("Actions");
            foreach (FrameworkElement child in actions.Children)
            {
                var right = child.TranslatePoint(new Point(child.ActualWidth, 0), explorer).X;
                Assert.True(right <= explorer.ActualWidth, $"{child} ends at {right:0} of {explorer.ActualWidth:0}.");
            }

            panel.Close();
            return results;
        });

        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }
}
