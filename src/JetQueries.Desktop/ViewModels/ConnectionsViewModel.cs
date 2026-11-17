using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Desktop.Resources;
using JetQueries.Desktop.Services;

namespace JetQueries.Desktop.ViewModels;

/// <summary>A row of the connection tree: a folder or a connection.</summary>
public abstract partial class ExplorerNodeViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    public ObservableCollection<ExplorerNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;
}

/// <summary>A folder grouping connections.</summary>
public sealed class FolderNodeViewModel(string name) : ExplorerNodeViewModel(name);

/// <summary>A saved connection and its live state.</summary>
public sealed partial class ConnectionNodeViewModel(ConnectionDefinition definition) : ExplorerNodeViewModel(definition.Name)
{
    public ConnectionDefinition Definition { get; } = definition;

    public bool IsReadOnly => Definition.IsReadOnly;

    public bool IsDevelopment => Definition.Environment == ConnectionEnvironment.Development;

    public bool IsTest => Definition.Environment == ConnectionEnvironment.Test;

    public bool IsProduction => Definition.Environment == ConnectionEnvironment.Production;

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Why the last connection attempt failed; empty when there is nothing to report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = string.Empty;

    public bool HasError => Error.Length > 0;
}

/// <summary>View model of the connection tree in the explorer panel.</summary>
public partial class ConnectionsViewModel(
    IConnectionStore store,
    IConnectionManager manager,
    ICredentialStore credentials,
    IJobQueue jobs,
    IDialogService dialogs) : ObservableObject
{
    public ObservableCollection<ExplorerNodeViewModel> Roots { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand), nameof(ToggleConnectionCommand))]
    [NotifyPropertyChangedFor(nameof(ToggleConnectionText))]
    public partial ExplorerNodeViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public string ToggleConnectionText => Selected is ConnectionNodeViewModel { IsConnected: true } ? Strings.ConnectionDisconnect : Strings.ConnectionConnect;

    /// <summary>Reads the saved connections and rebuilds the tree, keeping live state where the connection still exists.</summary>
    public async Task LoadAsync()
    {
        var job = jobs.Enqueue("Load connections", context => store.GetAllAsync(context.CancellationToken), JobPriority.High);
        var definitions = await job.Completion;

        var open = manager.Sessions.Select(static s => s.Definition.Id).ToHashSet();
        var selectedId = (Selected as ConnectionNodeViewModel)?.Definition.Id;
        Roots.Clear();
        ExplorerNodeViewModel? reselect = null;
        foreach (var definition in definitions)
        {
            var siblings = Roots;
            foreach (var part in definition.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var folder = siblings.OfType<FolderNodeViewModel>().FirstOrDefault(f => string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase));
                if (folder is null)
                {
                    folder = new FolderNodeViewModel(part);

                    // Folders stay above connections at every level.
                    siblings.Insert(siblings.Count(static n => n is FolderNodeViewModel), folder);
                }

                siblings = folder.Children;
            }

            var node = new ConnectionNodeViewModel(definition) { IsConnected = open.Contains(definition.Id) };
            siblings.Add(node);
            if (definition.Id == selectedId)
            {
                reselect = node;
            }
        }

        Selected = reselect;
        IsEmpty = definitions.Count == 0;
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task NewAsync()
    {
        if (await dialogs.EditConnectionAsync(null))
        {
            await LoadAsync();
        }
    }

    private bool HasConnectionSelected() => Selected is ConnectionNodeViewModel { IsBusy: false };

    [RelayCommand(CanExecute = nameof(HasConnectionSelected))]
    private async Task EditAsync()
    {
        if (Selected is ConnectionNodeViewModel node && await dialogs.EditConnectionAsync(node.Definition))
        {
            await LoadAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasConnectionSelected))]
    private async Task DeleteAsync()
    {
        if (Selected is not ConnectionNodeViewModel node)
        {
            return;
        }

        var message = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DeleteConfirmMessage), node.Name);
        if (!await dialogs.ConfirmAsync(Strings.DeleteConfirmTitle, message))
        {
            return;
        }

        var id = node.Definition.Id;
        var job = jobs.Enqueue(
            "Delete connection",
            async context =>
            {
                await manager.CloseAsync(id).ConfigureAwait(false);
                await store.DeleteAsync(id, context.CancellationToken).ConfigureAwait(false);
                await credentials.DeletePasswordAsync(id, context.CancellationToken).ConfigureAwait(false);
            },
            JobPriority.High);
        await job.Completion;
        await LoadAsync();
    }

    /// <summary>Connects the selected connection, or disconnects it when it is connected.</summary>
    [RelayCommand(CanExecute = nameof(HasConnectionSelected))]
    private async Task ToggleConnectionAsync()
    {
        if (Selected is not ConnectionNodeViewModel node)
        {
            return;
        }

        node.IsBusy = true;
        node.Error = string.Empty;
        RefreshCommands();
        try
        {
            if (node.IsConnected)
            {
                await jobs.Enqueue("Disconnect", _ => manager.CloseAsync(node.Definition.Id), JobPriority.High).Completion;
                node.IsConnected = false;
                return;
            }

            var attempt = await OpenAsync(node.Definition, null);
            if (attempt.Outcome == ConnectionOutcome.PasswordRequired)
            {
                // Asked for each time, or the stored entry is gone: prompt, and use the answer for this session only.
                var password = await dialogs.PromptPasswordAsync(node.Name);
                if (password is null)
                {
                    return;
                }

                attempt = await OpenAsync(node.Definition, password);
            }

            node.IsConnected = attempt.Outcome == ConnectionOutcome.Succeeded;
            node.Error = node.IsConnected ? string.Empty : ConnectionEditorViewModel.Describe(attempt);
        }
        catch (OperationCanceledException)
        {
            node.Error = Strings.OutcomeCanceled;
        }
        finally
        {
            node.IsBusy = false;
            RefreshCommands();
        }
    }

    private Task<ConnectionAttempt> OpenAsync(ConnectionDefinition definition, string? password) =>
        jobs.Enqueue("Connect", context => manager.OpenAsync(definition, password, context.CancellationToken), JobPriority.High).Completion;

    private void RefreshCommands()
    {
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ToggleConnectionText));
    }
}
