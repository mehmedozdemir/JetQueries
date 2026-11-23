using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A connection found in another tool's settings.</summary>
/// <param name="Source">"DBeaver" or "SSMS".</param>
/// <param name="Name">Its name there.</param>
/// <param name="Folder">Its folder or group path there ("" for none).</param>
/// <param name="ProviderId">The JetQueries provider it belongs to.</param>
/// <param name="Endpoint">Where it connects; never a password.</param>
/// <param name="UserName">The login, when the tool kept it in plain sight.</param>
/// <param name="Environment">Development / test / production, when the tool says.</param>
public sealed record ImportCandidate(
    string Source, string Name, string Folder, string ProviderId, ImportedEndpoint Endpoint, string? UserName, ConnectionEnvironment Environment)
{
    /// <summary>"host:port / database · folder · user", what is known of it, for the list.</summary>
    public string Details => string.Join("  ·  ", new[] { Target, Folder, UserName }.Where(static t => !string.IsNullOrEmpty(t)));

    /// <summary>"host:port / database" for the list.</summary>
    public string Target => string.Join(
        " / ",
        new[] { Endpoint.File ?? (Endpoint.Port is { } port ? $"{Endpoint.Host}:{port.ToString(CultureInfo.InvariantCulture)}" : Endpoint.Host), Endpoint.Database ?? Endpoint.ServiceName ?? Endpoint.Sid }
            .Where(static t => !string.IsNullOrEmpty(t)));
}

/// <summary>
/// Reads connections from other tools: DBeaver's <c>data-sources*.json</c> and SSMS's registered servers
/// (<c>RegSrvr*.xml</c>). Passwords are left behind: DBeaver keeps them encrypted elsewhere, SSMS encrypts them
/// for the Windows user; the user types them at the first connect (and may save them in the credential store).
/// </summary>
public static class ConnectionImporter
{
    /// <summary>The settings files of other tools on this machine, with the tool's name.</summary>
    public static IReadOnlyList<(string Source, string Path)> Discover()
    {
        var found = new List<(string, string)>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dbeaver = Path.Combine(appData, "DBeaverData", "workspace6", "General", ".dbeaver");
        if (Directory.Exists(dbeaver))
        {
            found.AddRange(Directory.EnumerateFiles(dbeaver, "data-sources*.json").Select(static f => ("DBeaver", f)));
        }

        var ssms = Path.Combine(appData, "Microsoft", "SQL Server Management Studio");
        if (Directory.Exists(ssms))
        {
            found.AddRange(Directory.EnumerateFiles(ssms, "RegSrvr*.xml", SearchOption.AllDirectories).Select(static f => ("SSMS", f)));
        }

        return found;
    }

    /// <summary>The connections in a file: JSON is read as DBeaver's, XML as SSMS's.</summary>
    public static IReadOnlyList<ImportCandidate> Read(string path, string text) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ReadDBeaver(text) : ReadSsms(text);

    /// <summary>DBeaver's data-sources.json: SQL Server, Oracle, PostgreSQL and SQLite connections.</summary>
    public static IReadOnlyList<ImportCandidate> ReadDBeaver(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (!document.RootElement.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<ImportCandidate>();
        foreach (var connection in connections.EnumerateObject())
        {
            var item = connection.Value;
            var provider = String(item, "provider")?.ToUpperInvariant();
            var driver = String(item, "driver")?.ToUpperInvariant() ?? string.Empty;
            var providerId = provider switch
            {
                "SQLSERVER" or "MSSQL" => "sqlserver",
                "ORACLE" => "oracle",
                "POSTGRESQL" => "postgresql",
                "SQLITE" => "sqlite",
                "GENERIC" when driver.Contains("SQLITE", StringComparison.Ordinal) => "sqlite",
                _ => null,
            };
            if (providerId is null || !item.TryGetProperty("configuration", out var configuration))
            {
                continue;
            }

            var database = String(configuration, "database");
            var sidOrService = configuration.TryGetProperty("provider-properties", out var properties) ? String(properties, "@dbeaver-sid-service@") : null;
            var auth = String(configuration, "auth-model") ?? string.Empty;
            var url = String(configuration, "url");
            var endpoint = new ImportedEndpoint
            {
                Host = String(configuration, "host"),
                Port = int.TryParse(String(configuration, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null,
                Database = providerId == "oracle" ? null : database,
                ServiceName = providerId == "oracle" && !string.Equals(sidOrService, "SID", StringComparison.OrdinalIgnoreCase) ? database : null,
                Sid = providerId == "oracle" && string.Equals(sidOrService, "SID", StringComparison.OrdinalIgnoreCase) ? database : null,
                File = providerId == "sqlite" ? database ?? url?.Replace("jdbc:sqlite:", string.Empty, StringComparison.OrdinalIgnoreCase) : null,
                IntegratedSecurity = providerId == "sqlserver" && (auth.Contains("ntlm", StringComparison.OrdinalIgnoreCase) || auth.Contains("windows", StringComparison.OrdinalIgnoreCase)),
            };
            var environment = String(configuration, "type")?.ToUpperInvariant() switch
            {
                "DEV" => ConnectionEnvironment.Development,
                "TEST" => ConnectionEnvironment.Test,
                "PROD" => ConnectionEnvironment.Production,
                _ => ConnectionEnvironment.Unspecified,
            };
            result.Add(new ImportCandidate(
                "DBeaver", String(item, "name") ?? connection.Name, String(item, "folder") ?? string.Empty, providerId, endpoint, String(configuration, "user"), environment));
        }

        return result;
    }

    /// <summary>SSMS's registered servers (database engine only); the group path becomes the folder.</summary>
    public static IReadOnlyList<ImportCandidate> ReadSsms(string xml)
    {
        XNamespace rs = "http://schemas.microsoft.com/sqlserver/RegisteredServers/2007/08";
        XNamespace sml = "http://schemas.serviceml.org/sml/2007/02";
        var result = new List<ImportCandidate>();
        foreach (var server in XDocument.Parse(xml).Descendants(rs + "RegisteredServer"))
        {
            var type = (string?)server.Element(rs + "ServerType");
            if (type is not null && !type.Equals("DatabaseEngine", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Read through the generic builder (it understands quoting); the password in it is never used.
            var builder = new DbConnectionStringBuilder();
            try
            {
                builder.ConnectionString = (string?)server.Element(rs + "ConnectionStringWithEncryptedPassword") ?? string.Empty;
            }
            catch (ArgumentException)
            {
                continue;
            }

            string? Value(string key) => builder.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
            bool? Flag(string key) => Value(key) is { } value ? value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("sspi", StringComparison.OrdinalIgnoreCase) || value.Equals("mandatory", StringComparison.OrdinalIgnoreCase) || value.Equals("strict", StringComparison.OrdinalIgnoreCase) : null;
            var host = Value("data source") ?? (string?)server.Element(rs + "ServerName");
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            var integrated = Flag("integrated security") == true;
            var parent = server.Element(rs + "Parent")?.Descendants(sml + "Uri").FirstOrDefault()?.Value ?? string.Empty;
            var groups = parent.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var start = Array.FindIndex(groups, static g => g.Equals("DatabaseEngineServerGroup", StringComparison.OrdinalIgnoreCase));
            var folder = start < 0 ? string.Empty : string.Join('/', groups.Skip(start + 1).Where(static g => !g.Equals("ServerGroup", StringComparison.OrdinalIgnoreCase)));
            result.Add(new ImportCandidate(
                "SSMS",
                (string?)server.Element(rs + "Name") ?? host,
                folder,
                "sqlserver",
                new ImportedEndpoint
                {
                    Host = host,
                    Database = Value("initial catalog"),
                    IntegratedSecurity = integrated,
                    Encrypt = Flag("encrypt"),
                    TrustServerCertificate = Flag("trust server certificate"),
                },
                integrated ? null : Value("user id"),
                ConnectionEnvironment.Unspecified));
        }

        return result;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}

/// <summary>A connection in the import list.</summary>
public sealed partial class ImportItem(ImportCandidate candidate, bool exists, bool supported) : ObservableObject
{
    public ImportCandidate Candidate { get; } = candidate;

    /// <summary>The same connection is saved already.</summary>
    public bool Exists { get; } = exists;

    /// <summary>A provider for it is loaded.</summary>
    public bool Supported { get; } = supported;

    public bool CanImport => Supported && !Exists;

    /// <summary>"exists", "no provider" or nothing.</summary>
    public string Note => !Supported ? Strings.ImportNoProvider : Exists ? Strings.ImportExists : string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = supported && !exists;
}

/// <summary>
/// Import connections: finds DBeaver and SSMS settings on the machine (or a file the user picks), lists their
/// connections, and saves the ones chosen as JetQueries connections (folders and environment kept, no passwords).
/// </summary>
public sealed partial class ImportConnectionsViewModel : ObservableObject
{
    private readonly IReadOnlyCollection<IDatabaseProvider> _providers;
    private readonly IConnectionStore _store;
    private readonly IJobQueue _jobs;
    private readonly Func<IReadOnlyList<(string Source, string Path)>> _discover;
    private List<ConnectionDefinition> _existing = [];

    /// <param name="providers">The loaded providers; a connection of another kind cannot be imported.</param>
    /// <param name="store">Where connections are saved.</param>
    /// <param name="jobs">Where files are read and connections saved.</param>
    /// <param name="discover">Where to look for other tools' settings; this computer's by default.</param>
    public ImportConnectionsViewModel(
        IReadOnlyCollection<IDatabaseProvider> providers, IConnectionStore store, IJobQueue jobs, Func<IReadOnlyList<(string Source, string Path)>>? discover = null)
    {
        _discover = discover ?? ConnectionImporter.Discover;
        _providers = providers;
        _store = store;
        _jobs = jobs;
    }

    /// <summary>Raised when the window should close; true when something was imported.</summary>
    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<ImportItem> Items { get; } = [];

    /// <summary>Which files were read ("DBeaver: …data-sources.json").</summary>
    [ObservableProperty]
    public partial string SourcesText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial int SelectedCount { get; set; }

    /// <summary>Reads the settings of the tools found on the machine.</summary>
    public async Task DiscoverAsync()
    {
        _existing = [.. await _jobs.Enqueue("Read connections", context => _store.GetAllAsync(context.CancellationToken), JobPriority.High).Completion];
        foreach (var (source, path) in await _jobs.Enqueue("Find other tools", _ => Task.FromResult(_discover()), JobPriority.High).Completion)
        {
            await AddFileAsync(path, source);
        }

        if (Items.Count == 0)
        {
            SourcesText = Strings.ImportNothingFound;
        }
    }

    /// <summary>Reads one file (DBeaver's JSON or SSMS's XML) the user chose.</summary>
    public async Task AddFileAsync(string path, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        IReadOnlyList<ImportCandidate> candidates;
        try
        {
            candidates = await _jobs.Enqueue(
                "Read " + Path.GetFileName(path),
                async context => ConnectionImporter.Read(path, await File.ReadAllTextAsync(path, context.CancellationToken).ConfigureAwait(false)),
                JobPriority.High).Completion;
        }
#pragma warning disable CA1031 // A file that cannot be read or parsed is named in the list; the others stay.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SourcesText = AppendLine(SourcesText, Path.GetFileName(path) + ": " + ex.Message);
            return;
        }

        foreach (var candidate in candidates)
        {
            var provider = _providers.FirstOrDefault(p => p.Id == candidate.ProviderId) as IConnectionImport;
            var settings = provider?.ToSettings(candidate.Endpoint);
            var exists = settings is not null && _existing.Any(d => d.ProviderId == candidate.ProviderId &&
                string.Equals(d.UserName ?? string.Empty, candidate.UserName ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                d.Settings.Count == settings.Count && settings.All(s => d.Settings.TryGetValue(s.Key, out var v) && string.Equals(v, s.Value, StringComparison.OrdinalIgnoreCase)));
            var item = new ImportItem(candidate, exists, provider is not null);
            item.PropertyChanged += (_, _) => Count();
            Items.Add(item);
        }

        SourcesText = AppendLine(SourcesText, string.Format(CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.ImportFileRead), source ?? Strings.ImportFile, path, candidates.Count));
        Count();
    }

    private bool CanImport() => SelectedCount > 0;

    /// <summary>Saves the chosen connections (no passwords; the first connect asks for one).</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        var chosen = Items.Where(static i => i.IsSelected && i.CanImport).ToList();
        foreach (var item in chosen)
        {
            var candidate = item.Candidate;
            var provider = (IConnectionImport)_providers.First(p => p.Id == candidate.ProviderId);
            var definition = new ConnectionDefinition
            {
                Id = Guid.NewGuid(),
                Name = candidate.Name,
                ProviderId = candidate.ProviderId,
                Settings = new Dictionary<string, string>(provider.ToSettings(candidate.Endpoint)),
                Folder = candidate.Folder,
                Environment = candidate.Environment,
                UserName = candidate.UserName,
                IsReadOnly = candidate.Environment == ConnectionEnvironment.Production,
            };
            await _jobs.Enqueue("Save connection", context => _store.SaveAsync(definition, context.CancellationToken), JobPriority.High).Completion;
        }

        CloseRequested?.Invoke(this, chosen.Count > 0);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items.Where(static i => i.CanImport))
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private void Count() => SelectedCount = Items.Count(static i => i.IsSelected && i.CanImport);

    private static string AppendLine(string text, string line) => text.Length == 0 ? line : text + "\n" + line;
}
