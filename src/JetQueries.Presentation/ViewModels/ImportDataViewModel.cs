using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A column of the file a table column can take its values from; index -1 skips the table column.</summary>
public sealed record ImportSourceOption(int Index, string Text);

/// <summary>A column of the table and the file column it is filled from.</summary>
public sealed partial class ImportMappingViewModel(ColumnInfo target) : ObservableObject
{
    public ColumnInfo Target { get; } = target;

    public string Name => Target.Name;

    public string DataType => Target.DataType + (Target.IsNullable ? string.Empty : " NOT NULL");

    [ObservableProperty]
    public partial ImportSourceOption? Source { get; set; }
}

/// <summary>
/// The Import data dialog of a table: a CSV file (encoding, separator, header line), its first rows, which file
/// column fills which table column, and the import itself in the background: values converted to the columns'
/// types and sent as parameters in multi-row INSERTs, in one transaction when the database allows it.
/// </summary>
public sealed partial class ImportDataViewModel : ObservableObject
{
    private const int PreviewRecords = 20;

    private static readonly CompositeFormat ColumnName = CompositeFormat.Parse(Strings.ImportColumnN);

    private readonly ConnectionSession _session;
    private readonly IMetadataService _metadata;
    private readonly IJobQueue _jobs;
    private readonly IDialogService? _dialogs;
    private JobHandle<long>? _run;

    static ImportDataViewModel() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public ImportDataViewModel(ConnectionSession session, DatabaseObject table, IMetadataService metadata, IJobQueue jobs, IDialogService? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(table);
        _session = session;
        _metadata = metadata;
        _jobs = jobs;
        _dialogs = dialogs;
        Table = table;
        Mark = ProviderMark.For(session.Provider);
        Encodings = [new("utf-8", "UTF-8"), new("windows-1254", Strings.ImportEncodingTurkish)];
        Delimiters = [new('\0', Strings.ImportDelimiterAuto), new(',', ","), new(';', ";"), new('\t', Strings.ExportDelimiterTab), new('|', "|")];
        SelectedEncoding = Encodings[0];
        SelectedDelimiter = Delimiters[0];
        Status = string.Empty;
    }

    /// <summary>Whether a table of this connection can be filled from a file.</summary>
    public static bool CanImport(ConnectionSession session, DatabaseObject target) =>
        session?.Provider is IBulkInsertScripting && target?.Kind == DatabaseObjectKinds.Table;

    public DatabaseObject Table { get; }

    public ProviderMark Mark { get; }

    public string Title => string.Join('.', new[] { Table.Name.Schema, Table.Name.Name }.Where(static p => !string.IsNullOrEmpty(p)));

    public string Subtitle => string.Join(" · ", new[] { _session.Definition.Name, Table.Name.Database }.Where(static p => !string.IsNullOrEmpty(p)));

    public IReadOnlyList<SettingChoice<string>> Encodings { get; }

    public IReadOnlyList<SettingChoice<char>> Delimiters { get; }

    [ObservableProperty]
    public partial string FilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SettingChoice<string> SelectedEncoding { get; set; }

    [ObservableProperty]
    public partial SettingChoice<char> SelectedDelimiter { get; set; }

    [ObservableProperty]
    public partial bool HasHeader { get; set; } = true;

    [ObservableProperty]
    public partial bool EmptyIsNull { get; set; } = true;

    /// <summary>All rows or none: the import runs in one transaction (when the database has them).</summary>
    [ObservableProperty]
    public partial bool UseTransaction { get; set; } = true;

    /// <summary>The file's columns (its header line, or Column 1, 2, …).</summary>
    public ObservableCollection<string> PreviewHeaders { get; } = [];

    /// <summary>The file's first records.</summary>
    public ObservableCollection<string[]> PreviewRows { get; } = [];

    /// <summary>"(skip)" and the file's columns.</summary>
    public ObservableCollection<ImportSourceOption> Sources { get; } = [];

    public ObservableCollection<ImportMappingViewModel> Mappings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(CancelCommand))]
    public partial bool IsImporting { get; set; }

    /// <summary>Rows written so far.</summary>
    [ObservableProperty]
    public partial long Imported { get; set; }

    /// <summary>What happened (rows written, or why it stopped).</summary>
    [ObservableProperty]
    public partial string Status { get; set; }

    [ObservableProperty]
    public partial bool Failed { get; set; }

    /// <summary>The table's columns, read from the catalog.</summary>
    public async Task LoadAsync()
    {
        var name = Table.Name;
        var database = _session.HasMetadataPerDatabase ? name.Database : null;
        var columns = await _jobs.Enqueue(
            "Read columns",
            context => _metadata.GetColumnsAsync(_session, _session.HasMetadataPerDatabase ? name with { Database = null } : name, refresh: true, context.CancellationToken, database),
            JobPriority.High).Completion;
        Mappings.Clear();
        foreach (var column in columns.OrderBy(static c => c.Ordinal))
        {
            Mappings.Add(new ImportMappingViewModel(column));
        }

        Map();
        ImportCommand.NotifyCanExecuteChanged();
    }

    partial void OnFilePathChanged(string value) => _ = PreviewAsync();

    partial void OnSelectedEncodingChanged(SettingChoice<string> value) => _ = PreviewAsync();

    partial void OnSelectedDelimiterChanged(SettingChoice<char> value) => _ = PreviewAsync();

    partial void OnHasHeaderChanged(bool value) => _ = PreviewAsync();

    private int _previewVersion;

    /// <summary>Reads the first records of the file (in the background) and maps the columns by name or position.</summary>
    public async Task PreviewAsync()
    {
        var path = FilePath.Trim();
        var version = ++_previewVersion;
        if (path.Length == 0 || !Path.IsPathRooted(path))
        {
            return;
        }

        var encoding = Encoding.GetEncoding(SelectedEncoding.Value);
        var chosen = SelectedDelimiter.Value;
        try
        {
            var (delimiter, records) = await _jobs.Enqueue(
                "Read file",
                context => Task.FromResult(ReadFirst(path, encoding, chosen, PreviewRecords + 1)),
                JobPriority.High).Completion;
            if (version != _previewVersion)
            {
                return;
            }

            Failed = false;
            Status = string.Empty;
            PreviewHeaders.Clear();
            PreviewRows.Clear();
            var width = records.Count == 0 ? 0 : records.Max(static r => r.Length);
            var headers = HasHeader && records.Count > 0 ? records[0] : [.. Enumerable.Range(1, width).Select(static i => string.Format(CultureInfo.CurrentCulture, ColumnName, i))];
            foreach (var header in headers)
            {
                PreviewHeaders.Add(header);
            }

            foreach (var record in records.Skip(HasHeader ? 1 : 0))
            {
                PreviewRows.Add(record);
            }

            Sources.Clear();
            Sources.Add(new ImportSourceOption(-1, Strings.ImportSkip));
            for (var i = 0; i < headers.Length; i++)
            {
                Sources.Add(new ImportSourceOption(i, headers[i]));
            }

            DetectedDelimiter = delimiter;
            Map();
        }
#pragma warning disable CA1031 // A file that cannot be read is said so in the dialog.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
#pragma warning restore CA1031
        {
            Failed = true;
            Status = ex.Message;
        }

        ImportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The separator in use (the one chosen, or the one told from the first line).</summary>
    public char DetectedDelimiter { get; private set; } = ',';

    private static (char Delimiter, List<string[]> Records) ReadFirst(string path, Encoding encoding, char chosen, int count)
    {
        using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
        var delimiter = chosen;
        if (delimiter == '\0')
        {
            using var peek = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
            delimiter = CsvReader.DetectDelimiter(peek.ReadLine() ?? string.Empty);
        }

        return (delimiter, [.. CsvReader.Read(reader, delimiter).Take(count)]);
    }

    // A table column takes the file column of the same name (ignoring case, spaces and underscores), else, without a
    // header, the one in its position.
    private void Map()
    {
        if (Sources.Count == 0)
        {
            return;
        }

        static string Key(string name) => new([.. name.Where(static c => c is not (' ' or '_' or '-')).Select(char.ToUpperInvariant)]);
        for (var i = 0; i < Mappings.Count; i++)
        {
            var mapping = Mappings[i];
            mapping.Source = HasHeader
                ? Sources.FirstOrDefault(s => s.Index >= 0 && Key(s.Text) == Key(mapping.Name)) ?? Sources[0]
                : Sources.FirstOrDefault(s => s.Index == i) ?? Sources[0];
        }
    }

    private bool CanStartImport() => !IsImporting && Mappings.Any(static m => m.Source is { Index: >= 0 }) && FilePath.Trim().Length > 0;

    /// <summary>Runs the import in the background; the dialog shows the rows written and the result.</summary>
    [RelayCommand(CanExecute = nameof(CanStartImport))]
    private async Task ImportAsync()
    {
        if (_session.Provider is not IBulkInsertScripting scripting)
        {
            return;
        }

        if (_session.Definition.Environment == ConnectionEnvironment.Production && _dialogs is not null &&
            !await _dialogs.ConfirmAsync(Strings.ImportConfirmTitle, string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ImportConfirmProduction), Title, _session.Definition.Name)))
        {
            return;
        }

        var mapped = Mappings.Where(static m => m.Source is { Index: >= 0 }).Select(static m => (m.Target, m.Source!.Index, Kind: ImportValues.KindOf(m.Target.DataType))).ToList();
        var path = FilePath.Trim();
        var encoding = Encoding.GetEncoding(SelectedEncoding.Value);
        var delimiter = DetectedDelimiter;
        var header = HasHeader;
        var emptyIsNull = EmptyIsNull;
        var transaction = UseTransaction;
        var session = _session;
        var table = _session.HasMetadataPerDatabase ? Table.Name with { Database = null } : Table.Name;
        var database = Table.Name.Database;
        var progress = new Progress<long>(count => Imported = count);
        IsImporting = true;
        Failed = false;
        Imported = 0;
        Status = Strings.ImportRunning;
        try
        {
            _run = _jobs.Enqueue(
                "Import data",
                context => RunAsync(session, scripting, table, database, mapped, path, encoding, delimiter, header, emptyIsNull, transaction, progress, context.CancellationToken),
                JobPriority.Normal);
            var written = await _run.Completion;
            Imported = written;
            Status = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ImportDone), written, Title);
        }
        catch (OperationCanceledException)
        {
            Failed = true;
            Status = transaction ? Strings.ImportCanceledRolledBack : Strings.ImportCanceled;
        }
#pragma warning disable CA1031 // The value's or the server's reason is shown in the dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Failed = true;
            Status = ex.Message + (transaction ? " " + Strings.ImportRolledBack : string.Empty);
        }
        finally
        {
            _run = null;
            IsImporting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsImporting))]
    private void Cancel() => _run?.Cancel();

    private static async Task<long> RunAsync(
        ConnectionSession session, IBulkInsertScripting scripting, DatabaseObjectName table, string? database,
        List<(ColumnInfo Target, int Index, ImportValues.Kind Kind)> mapped, string path, Encoding encoding, char delimiter, bool header, bool emptyIsNull, bool useTransaction,
        IProgress<long> progress, CancellationToken cancellationToken)
    {
        var channel = await session.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            if (database is not null && channel.ContextKind == DatabaseContextKind.Database)
            {
                await channel.ChangeDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            }

            var transaction = useTransaction && channel.SupportsTransactions;
            if (transaction)
            {
                await channel.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var columns = mapped.Select(static m => m.Target.Name).ToList();
                var perStatement = scripting.RowsPerStatement(columns.Count);
                var batch = new List<object?[]>(perStatement);
                long written = 0;
                var line = header ? 1 : 0;
                using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
                foreach (var record in CsvReader.Read(reader, delimiter).Skip(header ? 1 : 0))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    line++;
                    var values = new object?[mapped.Count];
                    for (var c = 0; c < mapped.Count; c++)
                    {
                        var (target, index, kind) = mapped[c];
                        var text = index < record.Length ? record[index] : string.Empty;
                        var (value, error) = ImportValues.Convert(text, kind, emptyIsNull);
                        if (error is not null)
                        {
                            throw new FormatException(string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ImportBadValue), line, target.Name, text, target.DataType));
                        }

                        values[c] = value;
                    }

                    batch.Add(values);
                    if (batch.Count == perStatement)
                    {
                        written += await InsertAsync(channel, scripting, table, columns, batch, line, cancellationToken).ConfigureAwait(false);
                        batch.Clear();
                        progress.Report(written);
                    }
                }

                if (batch.Count > 0)
                {
                    written += await InsertAsync(channel, scripting, table, columns, batch, line, cancellationToken).ConfigureAwait(false);
                    progress.Report(written);
                }

                if (transaction)
                {
                    await channel.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                return written;
            }
            catch
            {
                if (transaction)
                {
                    await channel.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    private static async Task<int> InsertAsync(
        QueryChannel channel, IBulkInsertScripting scripting, DatabaseObjectName table, List<string> columns, List<object?[]> rows, int lastLine, CancellationToken cancellationToken)
    {
        var sql = scripting.ScriptInsert(table, columns, rows.Count);
        var parameters = new List<QueryParameter>(rows.Count * columns.Count);
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                parameters.Add(new QueryParameter(string.Create(CultureInfo.InvariantCulture, $"p{r}_{c}"), rows[r][c]));
            }
        }

        try
        {
            await foreach (var _ in channel.ExecuteAsync(new QueryRequest(sql) { Parameters = parameters }, cancellationToken).ConfigureAwait(false))
            {
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Which lines the failing statement carried: the server's message names the value, not the line.
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ImportServerError), lastLine - rows.Count + 1, lastLine, ex.Message), ex);
        }

        return rows.Count;
    }
}
