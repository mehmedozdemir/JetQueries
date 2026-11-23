using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A property line of the details view.</summary>
/// <param name="Label">The translated label.</param>
/// <param name="Value">The value.</param>
public sealed record DetailPropertyView(string Label, string Value);

/// <summary>One table of the details view (a tab): translated headers and the provider's rows.</summary>
public sealed class DetailSectionView
{
    public DetailSectionView(ObjectDetailSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        Key = section.Key;
        Title = ObjectDetailsViewModel.Translate(section.Key, section.Title);
        Headers = [.. section.Columns.Select(static c => ObjectDetailsViewModel.Translate(c.Key, c.Title))];
        Rows = section.Rows;
        HasSource = section.Rows.Any(static r => r.Source is { Length: > 0 });
        HasTargets = section.Rows.Any(static r => r.Target is not null);
    }

    public string Key { get; }

    /// <summary>The tab title.</summary>
    public string Title { get; }

    /// <summary>The tab title with the row count, for example "Indexes (3)".</summary>
    public string Header => Rows.Count == 0 ? Title : Title + " (" + Rows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) + ")";

    public IReadOnlyList<string> Headers { get; }

    public IReadOnlyList<ObjectDetailRow> Rows { get; }

    /// <summary>Whether a row carries source text (shown under the table for the current row).</summary>
    public bool HasSource { get; }

    /// <summary>Whether a row can be opened (double-click opens its object's details).</summary>
    public bool HasTargets { get; }
}

/// <summary>
/// The details view of a database object, as DBeaver's object editor: properties, tabs of columns, constraints,
/// foreign keys, references, indexes, triggers, parameters and dependencies (as far as the provider gives them),
/// and the DDL. Everything is read as background jobs.
/// </summary>
public partial class ObjectDetailsViewModel : ObservableObject
{
    private readonly ConnectionSession _session;
    private readonly IMetadataService _metadata;
    private readonly IJobQueue _jobs;

    public ObjectDetailsViewModel(ConnectionSession session, DatabaseObject target, IMetadataService metadata, IJobQueue jobs)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        _session = session;
        _metadata = metadata;
        _jobs = jobs;
        Target = target;
        Mark = ProviderMark.For(session.Provider);
    }

    /// <summary>The object (its name as the catalog has it once loaded).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(HasData))]
    public partial DatabaseObject Target { get; set; }

    /// <summary>The connection's id, to find the window of the same object again.</summary>
    public Guid ConnectionId => _session.Definition.Id;

    public ProviderMark Mark { get; }

    /// <summary>The provider's language service, for colouring the DDL.</summary>
    public ISqlLanguageService? LanguageService => _session.Provider.LanguageService;

    /// <summary>schema.name.</summary>
    public string Title => Target.Name.Schema is { Length: > 0 } schema ? schema + "." + Target.Name.Name : Target.Name.Name;

    /// <summary>Kind · connection · database.</summary>
    public string Subtitle => string.Join(" · ", new[] { KindName(Target.Kind), _session.Definition.Name, Target.Name.Database }.Where(static p => !string.IsNullOrEmpty(p)));

    /// <summary>Tables and views have rows to show.</summary>
    public bool HasData => ObjectScripts.HasData(Target.Kind);

    public ObservableCollection<DetailPropertyView> Properties { get; } = [];

    public ObservableCollection<DetailSectionView> Sections { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Why the details could not be read; null when they were.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The definition (CREATE …); null while loading or when unavailable.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenDdlInEditorCommand))]
    public partial string? Ddl { get; set; }

    /// <summary>Shown in the DDL tab when there is no definition.</summary>
    [ObservableProperty]
    public partial string? DdlMessage { get; set; }

    /// <summary>Raised to open a query tab (the DDL, or the first rows).</summary>
    public event EventHandler<NewQueryRequest>? NewQueryRequested;

    /// <summary>Raised to open another object's details (a referenced table, a dependency).</summary>
    public event EventHandler<ObjectDetailsRequest>? DetailsRequested;

    /// <summary>Reads (or reads again) the details and the DDL.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        var target = Target;
        try
        {
            var detailsJob = _jobs.Enqueue("Object details", context => ReadDetailsAsync(target, context.CancellationToken), JobPriority.High).Completion;
            var ddlJob = _jobs.Enqueue("Object definition", context => ReadDdlAsync(target, context.CancellationToken), JobPriority.Normal).Completion;
            var details = await detailsJob;
            Properties.Clear();
            Sections.Clear();
            if (details is null)
            {
                Error = Format(Strings.DetailsNotFound, Title);
            }
            else
            {
                Target = details.Target;
                foreach (var property in details.Properties.Where(static p => !string.IsNullOrWhiteSpace(p.Value)))
                {
                    Properties.Add(new DetailPropertyView(Translate(property.Key, property.Label), property.Value!));
                }

                foreach (var section in details.Sections)
                {
                    Sections.Add(new DetailSectionView(section));
                }
            }

            try
            {
                Ddl = await ddlJob;
                DdlMessage = Ddl is null ? Format(Strings.DefinitionUnavailable, Target.Name) : null;
            }
#pragma warning disable CA1031 // The DDL tab says why; the other tabs stay.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                Ddl = null;
                DdlMessage = ex.Message;
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // The server's reason is shown in the window.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Opens the DDL in a query tab, to edit and run.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenDdl))]
    private void OpenDdlInEditor() =>
        NewQueryRequested?.Invoke(this, new NewQueryRequest(ConnectionId, ConnectionsViewModel.DatabaseOf(_session, Target)) { Text = Ddl!, Title = Target.Name.Name });

    private bool CanOpenDdl() => Ddl is not null;

    /// <summary>Opens a query tab with the first rows of the table or view and runs it.</summary>
    [RelayCommand]
    private async Task SelectRowsAsync()
    {
        var target = Target;
        try
        {
            var text = await _jobs.Enqueue("Write script", context => ObjectScripts.BuildAsync(_session, _metadata, target, ObjectScriptKind.SelectRows, context.CancellationToken), JobPriority.High)
                .Completion;
            if (text is not null)
            {
                NewQueryRequested?.Invoke(this, new NewQueryRequest(ConnectionId, ConnectionsViewModel.DatabaseOf(_session, target)) { Text = text, Title = target.Name.Name, Run = true });
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Shown in the window.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ex.Message;
        }
    }

    /// <summary>Opens the details of the object a row is about.</summary>
    public void OpenRow(ObjectDetailRow? row)
    {
        if (row?.Target is { } target)
        {
            DetailsRequested?.Invoke(this, new ObjectDetailsRequest(ConnectionId, target));
        }
    }

    /// <summary>The host's translation of a details key (<c>Detail_&lt;key&gt;</c>), else the provider's English text.</summary>
    public static string Translate(string key, string fallback) => Strings.Find("Detail_" + key) ?? fallback;

    /// <summary>The translated name of an object kind (Table, View, …).</summary>
    public static string KindName(DatabaseObjectKinds kind) => Strings.Find("Kind_" + kind) ?? kind.ToString();

    private async Task<ObjectDetails?> ReadDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        var details = await _session.UseMetadataAsync(
            (provider, token) => provider is IObjectDetailsProvider detailed ? detailed.GetDetailsAsync(target, token) : Task.FromResult<ObjectDetails?>(null),
            cancellationToken).ConfigureAwait(false);
        if (details is not null || !ObjectScripts.HasData(target.Kind))
        {
            return details;
        }

        // A provider without details: the columns it has.
        var columns = await _metadata.GetColumnsAsync(_session, target.Name, refresh: true, cancellationToken).ConfigureAwait(false);
        if (columns.Count == 0)
        {
            return null;
        }

        var section = new ObjectDetailSection(
            ObjectDetailKeys.Columns,
            "Columns",
            [new(ObjectDetailKeys.Position, "#"), new(ObjectDetailKeys.Name, "Name"), new(ObjectDetailKeys.DataType, "Data type"), new(ObjectDetailKeys.Nullable, "Nullable"), new(ObjectDetailKeys.Key, "Key")],
            [.. columns.OrderBy(static c => c.Ordinal).Select(static c => new ObjectDetailRow(
                [c.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Name, c.DataType, c.IsNullable ? "✓" : null, c.IsPrimaryKey ? "PK" : null]))]);
        return new ObjectDetails(target, [new ObjectProperty(ObjectDetailKeys.ObjectType, "Type", KindName(target.Kind))], [section]);
    }

    private Task<string?> ReadDdlAsync(DatabaseObject target, CancellationToken cancellationToken) =>
        _session.UseMetadataAsync((provider, token) => provider.GetDefinitionAsync(target, token), cancellationToken);

    private static string Format(string format, object? argument) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(format), argument);
}
