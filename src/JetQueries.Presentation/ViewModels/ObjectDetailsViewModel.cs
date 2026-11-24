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
        Section = section;
        Key = section.Key;
        Title = ObjectDetailsViewModel.Translate(section.Key, section.Title);
        Headers = [.. section.Columns.Select(static c => ObjectDetailsViewModel.Translate(c.Key, c.Title))];
        Rows = section.Rows;
        HasSource = section.Rows.Any(static r => r.Source is { Length: > 0 });
        HasTargets = section.Rows.Any(static r => r.Target is not null);
    }

    public string Key { get; }

    /// <summary>The provider's section this view shows.</summary>
    public ObjectDetailSection Section { get; }

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
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(HasData), nameof(HeaderText), nameof(ToolTip))]
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

    /// <summary>The tab title.</summary>
    public string HeaderText => Target.Name.Name;

    /// <summary>The tab's tooltip: schema.name, kind, connection and database.</summary>
    public string ToolTip => Title + "\n" + Subtitle;

    /// <summary>The environment of the object's connection, for the tab's colour dot.</summary>
    public ConnectionEnvironment Environment => _session.Definition.Environment;

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
    /// <remarks>
    /// A provider that reports its progress shows the properties and columns first; the other tabs appear as
    /// they are read (the bar under the header keeps moving until the last one).
    /// </remarks>
    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        var target = Target;
        var version = ++_loadVersion;
        var finished = false;
        var progress = new Progress<ObjectDetails>(partial =>
        {
            if (!finished && version == _loadVersion)
            {
                Show(partial);
            }
        });
        try
        {
            var detailsJob = _jobs.Enqueue("Object details", context => ReadDetailsAsync(target, progress, context.CancellationToken), JobPriority.High).Completion;
            var ddlJob = _jobs.Enqueue("Object definition", context => ReadDdlAsync(target, context.CancellationToken), JobPriority.Normal).Completion;
            var details = await detailsJob;
            finished = true;
            if (version != _loadVersion)
            {
                return;
            }

            if (details is null)
            {
                Properties.Clear();
                Sections.Clear();
                Error = Format(Strings.DetailsNotFound, Title);
            }
            else
            {
                Show(details);
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
            finished = true;
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    private int _loadVersion;

    /// <summary>
    /// Shows details read so far or in full. A section that is already shown keeps its tab (the tab the user is
    /// on stays selected); new sections are added in the provider's order.
    /// </summary>
    private void Show(ObjectDetails details)
    {
        Target = details.Target;
        Properties.Clear();
        foreach (var property in details.Properties.Where(static p => !string.IsNullOrWhiteSpace(p.Value)))
        {
            Properties.Add(new DetailPropertyView(Translate(property.Key, property.Label), property.Value!));
        }

        var keys = details.Sections.Select(static s => s.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = Sections.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(Sections[i].Key))
            {
                Sections.RemoveAt(i);
            }
        }

        for (var i = 0; i < details.Sections.Count; i++)
        {
            var section = details.Sections[i];
            if (i < Sections.Count && Sections[i].Key == section.Key)
            {
                if (!ReferenceEquals(Sections[i].Section, section))
                {
                    Sections[i] = new DetailSectionView(section);
                }
            }
            else
            {
                Sections.Insert(i, new DetailSectionView(section));
            }
        }

        while (Sections.Count > details.Sections.Count)
        {
            Sections.RemoveAt(Sections.Count - 1);
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

    private async Task<ObjectDetails?> ReadDetailsAsync(DatabaseObject target, IProgress<ObjectDetails> progress, CancellationToken cancellationToken)
    {
        // An object of another database (PostgreSQL) is read on a connection to that database.
        var asked = ForProvider(target);
        var details = await _session.UseMetadataAsync(
            target.Name.Database,
            (provider, token) => provider switch
            {
                IProgressiveObjectDetailsProvider progressive => progressive.GetDetailsAsync(asked, progress, token),
                IObjectDetailsProvider detailed => detailed.GetDetailsAsync(asked, token),
                _ => Task.FromResult<ObjectDetails?>(null),
            },
            cancellationToken).ConfigureAwait(false);
        details = Stamped(details, target);
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
        _session.UseMetadataAsync(target.Name.Database, (provider, token) => provider.GetDefinitionAsync(ForProvider(target), token), cancellationToken);

    // A provider whose metadata sees one database is asked without the database part, and its answer keeps it.
    private DatabaseObject ForProvider(DatabaseObject target) =>
        _session.HasMetadataPerDatabase ? target with { Name = target.Name with { Database = null } } : target;

    private ObjectDetails? Stamped(ObjectDetails? details, DatabaseObject target)
    {
        if (details is null || !_session.HasMetadataPerDatabase || target.Name.Database is not { } database)
        {
            return details;
        }

        DatabaseObject? Stamp(DatabaseObject? o) => o is null ? null : o with { Name = o.Name with { Database = database } };
        return details with
        {
            Target = Stamp(details.Target)!,
            Sections = [.. details.Sections.Select(s => s with { Rows = [.. s.Rows.Select(r => r with { Target = Stamp(r.Target) })] })],
        };
    }

    private static string Format(string format, object? argument) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(format), argument);
}
