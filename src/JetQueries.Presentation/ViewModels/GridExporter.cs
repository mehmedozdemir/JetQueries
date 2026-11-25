using System.Globalization;
using System.Text;
using JetQueries.Core.Jobs;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;

namespace JetQueries.Presentation.ViewModels;

/// <summary>
/// Exports the rows a DBA table shows (after its sorts and filters) through the same Export dialog and writer as
/// query results: Excel with its formatting, CSV, JSON, HTML, Markdown, SQL INSERT.
/// </summary>
public sealed class GridExporter(IDialogService dialogs, IJobQueue jobs)
{
    /// <summary>Asks for the format and the file, writes the rows off the UI thread, opens the file if asked.</summary>
    /// <param name="view">The table as shown.</param>
    /// <param name="name">The file name offered (the screen and the table, for example "Etkinlik - oturumlar").</param>
    /// <returns>Rows written; null when the dialog was cancelled or writing failed (the reason was shown).</returns>
    public async Task<long?> ExportAsync(GridRowsView view, string name)
    {
        ArgumentNullException.ThrowIfNull(view);
        var export = new ExportResultViewModel(view.RowCount, canReadAll: false, name, dialogs.DocumentsFolder);
        if (!await dialogs.ExportResultAsync(export) || !export.IsValid)
        {
            return null;
        }

        var options = export.Options;
        var path = export.Path.Trim();
        var headers = view.Headers.ToList();
        var rows = view.ExportRows().ToList(); // taken now: the table may refresh while the file is written
        try
        {
            var written = await jobs.Enqueue(
                "Export table",
                async context =>
                {
                    var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                    await using (file.ConfigureAwait(false))
                    {
                        return await ResultExporter.WriteAsync(file, options, headers, Async(rows), null, context.CancellationToken).ConfigureAwait(false);
                    }
                },
                JobPriority.Normal).Completion;
            if (export.OpenWhenDone)
            {
                dialogs.OpenWithShell(path);
            }

            return written;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
#pragma warning disable CA1031 // The file system's reason is shown; nothing else is disturbed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await dialogs.ConfirmAsync(Strings.ExportDialogTitle, string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ExportFailed), ex.Message));
            return null;
        }
    }

    private static async IAsyncEnumerable<object?[]> Async(IEnumerable<object?[]> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
