using CommunityToolkit.Mvvm.ComponentModel;
using JetQueries.Presentation.Resources;

namespace JetQueries.Presentation.ViewModels;

/// <summary>
/// The Export dialog of a result: the file format and its options, which rows (those shown, or the whole result
/// read again from the server), the file, and whether to open it afterwards.
/// </summary>
public sealed partial class ExportResultViewModel : ObservableObject
{
    private readonly string _folder;
    private readonly string _name;

    /// <param name="shownRows">Rows in the grid now (after its filters).</param>
    /// <param name="canReadAll">Whether the query can be read again in full (a single SELECT on an open connection).</param>
    /// <param name="name">The file name offered, without extension.</param>
    /// <param name="folder">The folder offered.</param>
    public ExportResultViewModel(int shownRows, bool canReadAll, string name, string folder)
    {
        ShownRows = shownRows;
        CanReadAll = canReadAll;
        _name = string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim() is { Length: > 0 } clean ? clean : "result";
        _folder = folder;
        Formats =
        [
            new(ResultExportFormat.Csv, "CSV"), new(ResultExportFormat.Excel, "Excel (.xlsx)"), new(ResultExportFormat.Json, "JSON"),
            new(ResultExportFormat.SqlInsert, "SQL INSERT"), new(ResultExportFormat.Markdown, "Markdown"), new(ResultExportFormat.Html, "HTML"),
        ];
        Delimiters = [new(",", Strings.ExportDelimiterComma), new(";", Strings.ExportDelimiterSemicolon), new("\t", Strings.ExportDelimiterTab)];
        Delimiter = Delimiters[0];
        SelectedFormat = Formats[0];
        Path = System.IO.Path.Combine(folder, _name + ".csv");
    }

    public IReadOnlyList<SettingChoice<ResultExportFormat>> Formats { get; }

    public IReadOnlyList<SettingChoice<string>> Delimiters { get; }

    public int ShownRows { get; }

    public bool CanReadAll { get; }

    /// <summary>"The rows shown (1 250)".</summary>
    public string ShownRowsText => string.Format(System.Globalization.CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.ExportShownRows), ShownRows);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCsv), nameof(IsSql), nameof(IsText))]
    public partial SettingChoice<ResultExportFormat> SelectedFormat { get; set; }

    [ObservableProperty]
    public partial SettingChoice<string> Delimiter { get; set; }

    [ObservableProperty]
    public partial bool Header { get; set; } = true;

    /// <summary>NULL written as the word NULL (else empty).</summary>
    [ObservableProperty]
    public partial bool NullAsText { get; set; }

    [ObservableProperty]
    public partial string TableName { get; set; } = "table_name";

    /// <summary>The whole result, read again from the server, instead of the rows shown.</summary>
    [ObservableProperty]
    public partial bool AllRows { get; set; }

    [ObservableProperty]
    public partial bool OpenWhenDone { get; set; } = true;

    [ObservableProperty]
    public partial string Path { get; set; }

    public bool IsCsv => SelectedFormat.Value == ResultExportFormat.Csv;

    public bool IsSql => SelectedFormat.Value == ResultExportFormat.SqlInsert;

    /// <summary>Formats where NULL has a written form to choose.</summary>
    public bool IsText => SelectedFormat.Value is ResultExportFormat.Csv or ResultExportFormat.Markdown;

    // The file name follows the format, unless the user named the file otherwise.
    partial void OnSelectedFormatChanged(SettingChoice<ResultExportFormat> value)
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            Path = System.IO.Path.Combine(_folder, _name + ResultExportOptions.Extension(value.Value));
            return;
        }

        var extension = System.IO.Path.GetExtension(Path);
        if (Formats.Any(f => string.Equals(ResultExportOptions.Extension(f.Value), extension, StringComparison.OrdinalIgnoreCase)) || extension.Length == 0)
        {
            Path = System.IO.Path.ChangeExtension(Path, ResultExportOptions.Extension(value.Value));
        }
    }

    /// <summary>The options as chosen.</summary>
    public ResultExportOptions Options => new()
    {
        Format = SelectedFormat.Value,
        Delimiter = Delimiter.Value,
        Header = Header,
        NullText = NullAsText ? "NULL" : string.Empty,
        TableName = string.IsNullOrWhiteSpace(TableName) ? "table_name" : TableName.Trim(),
    };

    /// <summary>Whether the dialog can be confirmed (a file is named).</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Path) && System.IO.Path.IsPathRooted(Path.Trim());
}
