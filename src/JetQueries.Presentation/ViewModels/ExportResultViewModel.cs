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
            new(ResultExportFormat.Excel, "Excel (.xlsx)"), new(ResultExportFormat.Csv, "CSV"), new(ResultExportFormat.Json, "JSON"),
            new(ResultExportFormat.SqlInsert, "SQL INSERT"), new(ResultExportFormat.Markdown, "Markdown"), new(ResultExportFormat.Html, "HTML"),
        ];
        Delimiters = [new(",", Strings.ExportDelimiterComma), new(";", Strings.ExportDelimiterSemicolon), new("\t", Strings.ExportDelimiterTab)];
        Delimiter = Delimiters[0];
        SelectedFormat = Formats[0];
        Path = System.IO.Path.Combine(folder, _name + ".xlsx");
        HeaderColors =
        [
            new(ExcelHeaderColor.Blue, Strings.ExportColorBlue), new(ExcelHeaderColor.Green, Strings.ExportColorGreen), new(ExcelHeaderColor.Orange, Strings.ExportColorOrange),
            new(ExcelHeaderColor.Gray, Strings.ExportColorGray), new(ExcelHeaderColor.Dark, Strings.ExportColorDark), new(ExcelHeaderColor.None, Strings.ExportColorNone),
        ];
        HeaderColor = HeaderColors[0];
        string[] styleNames =
        [
            Strings.ExportColorBlue, Strings.ExportStyleBlueDark, Strings.ExportColorGreen, Strings.ExportColorGray, Strings.ExportStyleBlack, Strings.ExportStyleLightBlue, Strings.ExportStylePlain,
        ];
        TableStyles = [.. ExcelExportOptions.TableStyles.Select((style, i) => new SettingChoice<string>(style, styleNames[i]))];
        TableStyle = TableStyles[0];
        var regional = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat;
        DateFormats =
        [
            new(ExcelDateFormat.Regional, string.Format(System.Globalization.CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.ExportExcelDateRegional),
                new DateTime(2026, 11, 25, 14, 30, 0).ToString(regional.ShortDatePattern + " " + regional.ShortTimePattern, System.Globalization.CultureInfo.CurrentCulture))),
            new(ExcelDateFormat.Iso, "2026-11-25 14:30:00"),
        ];
        DateFormat = DateFormats[0];
        SheetName = ResultExporter.SheetName(_name);
    }

    public IReadOnlyList<SettingChoice<ExcelHeaderColor>> HeaderColors { get; }

    public IReadOnlyList<SettingChoice<string>> TableStyles { get; }

    public IReadOnlyList<SettingChoice<ExcelDateFormat>> DateFormats { get; }

    // ---- Excel ----

    public bool IsExcel => SelectedFormat.Value == ResultExportFormat.Excel;

    [ObservableProperty]
    public partial string SheetName { get; set; }

    /// <summary>A title written above the table; none when empty.</summary>
    [ObservableProperty]
    public partial string ExcelTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool FreezeHeader { get; set; } = true;

    [ObservableProperty]
    public partial bool BoldHeader { get; set; } = true;

    [ObservableProperty]
    public partial SettingChoice<ExcelHeaderColor> HeaderColor { get; set; }

    [ObservableProperty]
    public partial bool AutoFilter { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanColorHeader))]
    public partial bool AsTable { get; set; }

    /// <summary>A header colour of its own (an Excel table's style colours its header).</summary>
    public bool CanColorHeader => !AsTable;

    [ObservableProperty]
    public partial SettingChoice<string> TableStyle { get; set; }

    [ObservableProperty]
    public partial bool Borders { get; set; }

    [ObservableProperty]
    public partial bool AutoFitColumns { get; set; } = true;

    [ObservableProperty]
    public partial bool ThousandsSeparator { get; set; } = true;

    [ObservableProperty]
    public partial SettingChoice<ExcelDateFormat> DateFormat { get; set; }

    [ObservableProperty]
    public partial bool Totals { get; set; }

    /// <summary>The choices worth keeping for the next export (not the file, the title or the rows).</summary>
    public ExportPreferences Preferences => new()
    {
        Format = SelectedFormat.Value,
        Delimiter = Delimiter.Value,
        Header = Header,
        OpenWhenDone = OpenWhenDone,
        FreezeHeader = FreezeHeader,
        BoldHeader = BoldHeader,
        HeaderColor = HeaderColor.Value,
        AutoFilter = AutoFilter,
        AsTable = AsTable,
        TableStyle = TableStyle.Value,
        Borders = Borders,
        AutoFitColumns = AutoFitColumns,
        ThousandsSeparator = ThousandsSeparator,
        DateFormat = DateFormat.Value,
        Totals = Totals,
    };

    /// <summary>Takes the choices of the last export.</summary>
    public void Apply(ExportPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        SelectedFormat = Formats.FirstOrDefault(f => f.Value == preferences.Format) ?? SelectedFormat;
        Delimiter = Delimiters.FirstOrDefault(d => d.Value == preferences.Delimiter) ?? Delimiter;
        Header = preferences.Header;
        OpenWhenDone = preferences.OpenWhenDone;
        FreezeHeader = preferences.FreezeHeader;
        BoldHeader = preferences.BoldHeader;
        HeaderColor = HeaderColors.FirstOrDefault(c => c.Value == preferences.HeaderColor) ?? HeaderColor;
        AutoFilter = preferences.AutoFilter;
        AsTable = preferences.AsTable;
        TableStyle = TableStyles.FirstOrDefault(t => t.Value == preferences.TableStyle) ?? TableStyle;
        Borders = preferences.Borders;
        AutoFitColumns = preferences.AutoFitColumns;
        ThousandsSeparator = preferences.ThousandsSeparator;
        DateFormat = DateFormats.FirstOrDefault(d => d.Value == preferences.DateFormat) ?? DateFormat;
        Totals = preferences.Totals;
    }

    public IReadOnlyList<SettingChoice<ResultExportFormat>> Formats { get; }

    public IReadOnlyList<SettingChoice<string>> Delimiters { get; }

    public int ShownRows { get; }

    public bool CanReadAll { get; }

    /// <summary>"The rows shown (1 250)".</summary>
    public string ShownRowsText => string.Format(System.Globalization.CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.ExportShownRows), ShownRows);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCsv), nameof(IsSql), nameof(IsText), nameof(IsExcel))]
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
        Excel = new ExcelExportOptions
        {
            SheetName = ResultExporter.SheetName(SheetName),
            Title = string.IsNullOrWhiteSpace(ExcelTitle) ? null : ExcelTitle.Trim(),
            FreezeHeader = FreezeHeader,
            BoldHeader = BoldHeader,
            HeaderColor = HeaderColor.Value,
            AutoFilter = AutoFilter,
            AsTable = AsTable,
            TableStyle = TableStyle.Value,
            Borders = Borders,
            AutoFitColumns = AutoFitColumns,
            ThousandsSeparator = ThousandsSeparator,
            DateFormat = DateFormat.Value,
            Totals = Totals,
            TotalsLabel = Strings.ExportExcelTotal,
        },
    };

    /// <summary>Whether the dialog can be confirmed (a file is named).</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Path) && System.IO.Path.IsPathRooted(Path.Trim());
}

/// <summary>The export choices kept between exports (settings section "export").</summary>
public sealed record ExportPreferences
{
    /// <summary>The settings section.</summary>
    public const string Section = "export";

    public ResultExportFormat Format { get; init; } = ResultExportFormat.Excel;

    public string Delimiter { get; init; } = ",";

    public bool Header { get; init; } = true;

    public bool OpenWhenDone { get; init; } = true;

    public bool FreezeHeader { get; init; } = true;

    public bool BoldHeader { get; init; } = true;

    public ExcelHeaderColor HeaderColor { get; init; } = ExcelHeaderColor.Blue;

    public bool AutoFilter { get; init; } = true;

    public bool AsTable { get; init; }

    public string TableStyle { get; init; } = "TableStyleMedium2";

    public bool Borders { get; init; }

    public bool AutoFitColumns { get; init; } = true;

    public bool ThousandsSeparator { get; init; } = true;

    public ExcelDateFormat DateFormat { get; init; } = ExcelDateFormat.Regional;

    public bool Totals { get; init; }
}
