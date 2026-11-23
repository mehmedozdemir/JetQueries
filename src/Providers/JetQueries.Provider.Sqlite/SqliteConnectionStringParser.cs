using JetQueries.Providers.Connections;
using JetQueries.Sdk;

namespace JetQueries.Provider.Sqlite;

/// <summary>
/// Reads a Microsoft.Data.Sqlite / System.Data.SQLite connection string (<c>Data Source=app.db;Mode=ReadOnly</c>), a
/// JDBC URL (<c>jdbc:sqlite:C:\data\app.db</c>) or a bare file path.
/// </summary>
internal static class SqliteConnectionStringParser
{
    private static readonly HashSet<string> Tuning = new(StringComparer.Ordinal)
    {
        "CACHE", "POOLING", "DEFAULTTIMEOUT", "COMMANDTIMEOUT", "RECURSIVETRIGGERS", "VERSION", "JOURNALMODE", "SYNCHRONOUS",
        "PAGESIZE", "CACHESIZE",
    };

    public static ParsedConnectionString Parse(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var text = connectionString.Trim();
        if (text.Length == 0)
        {
            throw new FormatException("The connection string is empty.");
        }

        if (ConnectionStringText.TryStrip(text, out var path, "jdbc:sqlite:"))
        {
            return File(path);
        }

        if (!text.Contains('=', StringComparison.Ordinal))
        {
            return File(text);
        }

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = new List<string>();
        foreach (var (key, value) in ConnectionStringText.ReadPairs(text))
        {
            switch (ConnectionStringText.Normalize(key))
            {
                case "DATASOURCE" or "FILENAME" or "FILE" or "URI":
                    settings[SqliteConnectionSettings.File] = Path(value);
                    break;
                case "MODE":
                    settings[SqliteConnectionSettings.Mode] = value.Trim().ToUpperInvariant() switch
                    {
                        "READWRITE" => "readWrite",
                        "READONLY" => "readOnly",
                        "READWRITECREATE" => "create",
                        "MEMORY" => throw new FormatException("In-memory databases are not supported: give a database file."),
                        _ => throw new FormatException("Mode must be ReadWrite, ReadOnly or ReadWriteCreate."),
                    };
                    break;
                case "READONLY":
                    if (ConnectionStringText.Flag(value) == true)
                    {
                        settings[SqliteConnectionSettings.Mode] = "readOnly";
                    }

                    break;
                case "FOREIGNKEYS":
                    if (ConnectionStringText.Flag(value) is { } enforce)
                    {
                        settings[SqliteConnectionSettings.ForeignKeys] = enforce ? "true" : "false";
                    }
                    else
                    {
                        ignored.Add(key);
                    }

                    break;
                case var other when Tuning.Contains(other):
                    break;
                default:
                    // Password (an encrypted database) among them: the provider opens plain files only.
                    ignored.Add(key);
                    break;
            }
        }

        return settings.ContainsKey(SqliteConnectionSettings.File)
            ? new ParsedConnectionString { Settings = settings, Ignored = ignored }
            : throw new FormatException("The connection string has no Data Source.");
    }

    private static ParsedConnectionString File(string path) => new()
    {
        Settings = new Dictionary<string, string>(StringComparer.Ordinal) { [SqliteConnectionSettings.File] = Path(path) },
    };

    private static string Path(string value)
    {
        var path = value.Trim().Trim('"');
        if (path.Length == 0)
        {
            throw new FormatException("The database file is empty.");
        }

        if (path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("In-memory databases are not supported: give a database file.");
        }

        // A file: URI names a local file.
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile)
            {
                throw new FormatException("Give the path of a database file.");
            }

            path = uri.LocalPath;
        }

        return path;
    }
}
