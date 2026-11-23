using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A database type the user can choose in the connection editor.</summary>
public sealed record ProviderOption(IDatabaseProvider Provider)
{
    public string DisplayName => Provider.DisplayName;

    /// <summary>One line under the name.</summary>
    public string Description { get; } = ProviderMark.DescriptionOf(Provider);

    public ProviderMark Mark { get; } = ProviderMark.For(Provider);

    /// <summary>Whether a connection string can be pasted for this type.</summary>
    public bool ReadsConnectionStrings => Provider is IConnectionStringImport;
}

/// <summary>An environment the user can choose in the connection editor.</summary>
public sealed record EnvironmentOption(ConnectionEnvironment Value, string DisplayName);

/// <summary>One provider-defined setting in the connection editor.</summary>
public partial class SettingFieldViewModel : ObservableObject
{
    public SettingFieldViewModel(ConnectionSettingDescriptor descriptor, string? value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;

        // The host's translation of a well-known key wins over the provider's English label.
        Label = Strings.Find("Setting_" + descriptor.Key) ?? descriptor.DisplayName;
        Value = value ?? descriptor.DefaultValue ?? string.Empty;
        SelectedOption = descriptor.Options.FirstOrDefault(o => string.Equals(o.Value, Value, StringComparison.OrdinalIgnoreCase));
    }

    public ConnectionSettingDescriptor Descriptor { get; }

    public string Key => Descriptor.Key;

    public string Label { get; }

    public string? Hint => Descriptor.Hint;

    public bool IsRequired => Descriptor.IsRequired;

    public bool IsText => Descriptor.Kind is ConnectionSettingKind.Text or ConnectionSettingKind.Number;

    public bool IsBoolean => Descriptor.Kind == ConnectionSettingKind.Boolean;

    public bool IsChoice => Descriptor.Kind == ConnectionSettingKind.Choice;

    public bool IsFilePath => Descriptor.Kind == ConnectionSettingKind.FilePath;

    /// <summary>
    /// Fine-tuning rather than where to connect: optional switches and numbers (timeouts, certificate trust…),
    /// shown under "Advanced" so that the form stays short.
    /// </summary>
    public bool IsAdvanced => !IsRequired && Descriptor.Kind is ConnectionSettingKind.Boolean or ConnectionSettingKind.Number;

    /// <summary>Whether the value differs from the descriptor's default (an advanced field like that opens its section).</summary>
    public bool IsChanged => !string.Equals(Value.Trim(), Descriptor.DefaultValue ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ConnectionSettingOption> Options => Descriptor.Options;

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial ConnectionSettingOption? SelectedOption { get; set; }

    public bool BooleanValue
    {
        get => bool.TryParse(Value, out var flag) && flag;
        set => Value = value ? "true" : "false";
    }

    partial void OnSelectedOptionChanged(ConnectionSettingOption? value)
    {
        if (value is not null)
        {
            Value = value.Value;
        }
    }

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(BooleanValue));
}

/// <summary>View model of the dialog that creates or edits a saved connection.</summary>
public partial class ConnectionEditorViewModel : ObservableObject
{
    private readonly ConnectionDefinition? _existing;
    private readonly IConnectionStore _store;
    private readonly ICredentialStore _credentials;
    private readonly IConnectionManager _manager;
    private readonly IJobQueue _jobs;
    private JobHandle? _test;

    public ConnectionEditorViewModel(
        ConnectionDefinition? existing,
        IEnumerable<IDatabaseProvider> providers,
        IConnectionStore store,
        ICredentialStore credentials,
        IConnectionManager manager,
        IJobQueue jobs)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _existing = existing;
        _store = store;
        _credentials = credentials;
        _manager = manager;
        _jobs = jobs;

        Providers = [.. providers.OrderBy(static p => p.DisplayName, StringComparer.CurrentCulture).Select(static p => new ProviderOption(p))];
        Environments =
        [
            new(ConnectionEnvironment.Unspecified, Strings.EnvironmentUnspecified),
            new(ConnectionEnvironment.Development, Strings.EnvironmentDevelopment),
            new(ConnectionEnvironment.Test, Strings.EnvironmentTest),
            new(ConnectionEnvironment.Production, Strings.EnvironmentProduction),
        ];

        Name = existing?.Name ?? string.Empty;
        Folder = existing?.Folder ?? string.Empty;
        IsReadOnly = existing?.IsReadOnly ?? false;
        UserName = existing?.UserName ?? string.Empty;
        SavePassword = existing?.SavePassword ?? true;
        Password = string.Empty;
        TestMessage = string.Empty;
        ValidationMessage = string.Empty;
        SelectedEnvironment = Environments.First(e => e.Value == (existing?.Environment ?? ConnectionEnvironment.Unspecified));
        SelectedProvider = Providers.FirstOrDefault(p => p.Provider.Id == existing?.ProviderId) ?? (Providers.Count == 1 ? Providers[0] : null);

        // A new connection starts with the choice of database type (unless there is nothing to choose).
        IsChoosingType = SelectedProvider is null;
        ConnectionString = string.Empty;
        ConnectionStringMessage = string.Empty;
    }

    /// <summary>Raised when a pasted connection string gave a password: the view puts it in its password box.</summary>
    public event EventHandler<string>? PasswordProvided;

    /// <summary>The first step: the list of database types is shown instead of the form.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingSettings))]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(SaveCommand))]
    public partial bool IsChoosingType { get; set; }

    /// <summary>The second step: the form of the chosen type.</summary>
    public bool IsEditingSettings => !IsChoosingType;

    /// <summary>The type of a saved connection is not changed (its settings belong to it).</summary>
    public bool CanChangeType => _existing is null && Providers.Count > 1 && !IsTesting;

    /// <summary>The connection string box is shown instead of the fields.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormMode))]
    public partial bool IsConnectionStringMode { get; set; }

    /// <summary>The fields are shown (the other side of <see cref="IsConnectionStringMode"/>).</summary>
    public bool IsFormMode
    {
        get => !IsConnectionStringMode;
        set => IsConnectionStringMode = !value;
    }

    /// <summary>The pasted text. It may hold a password: it is emptied as soon as it has been read.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyConnectionStringCommand))]
    public partial string ConnectionString { get; set; }

    /// <summary>What reading the connection string gave (or why it could not be read).</summary>
    [ObservableProperty]
    public partial string ConnectionStringMessage { get; set; }

    /// <summary>Whether <see cref="ConnectionStringMessage"/> is an error.</summary>
    [ObservableProperty]
    public partial bool ConnectionStringFailed { get; set; }

    [RelayCommand]
    private void ChooseType(ProviderOption? option)
    {
        if (option is null)
        {
            return;
        }

        SelectedProvider = option;
        IsConnectionStringMode = false;
        ConnectionStringMessage = string.Empty;
        ValidationMessage = string.Empty;
        IsChoosingType = false;
    }

    [RelayCommand]
    private void ChangeType()
    {
        if (CanChangeType && !IsTesting)
        {
            IsChoosingType = true;
        }
    }

    private bool CanApplyConnectionString() => !string.IsNullOrWhiteSpace(ConnectionString) && SelectedProvider?.Provider is IConnectionStringImport;

    /// <summary>Reads the pasted connection string into the fields, the user name and the password box.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyConnectionString))]
    private void ApplyConnectionString()
    {
        if (SelectedProvider?.Provider is not IConnectionStringImport reader)
        {
            return;
        }

        ParsedConnectionString parsed;
        try
        {
            parsed = reader.ParseConnectionString(ConnectionString);
        }
        catch (FormatException ex)
        {
            ConnectionStringFailed = true;
            ConnectionStringMessage = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ConnectionStringUnreadable), ex.Message);
            return;
        }

        // What the string does not say goes back to its default: the result is the pasted connection.
        foreach (var field in Fields)
        {
            var value = parsed.Settings.GetValueOrDefault(field.Key) ?? field.Descriptor.DefaultValue ?? string.Empty;
            field.Value = value;
            field.SelectedOption = field.Descriptor.Options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));
        }

        ShowAdvanced = Fields.Any(static f => f.IsAdvanced && f.IsChanged);
        UpdateNeedsCredential();
        if (NeedsCredential && parsed.UserName is { Length: > 0 } user)
        {
            UserName = user;
        }

        var gotPassword = NeedsCredential && parsed.Password is { Length: > 0 };
        if (gotPassword)
        {
            Password = parsed.Password!;
            PasswordProvided?.Invoke(this, parsed.Password!);
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = SuggestName(parsed.Settings);
        }

        var message = gotPassword ? Strings.ConnectionStringReadWithPassword : Strings.ConnectionStringRead;
        if (parsed.Ignored.Count > 0)
        {
            message += " " + string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ConnectionStringIgnored), string.Join(", ", parsed.Ignored));
        }

        ConnectionStringFailed = false;
        ConnectionStringMessage = message;
        ConnectionString = string.Empty;
        IsConnectionStringMode = false;
    }

    // "server / database", or the file name of a file database.
    private string SuggestName(IReadOnlyDictionary<string, string> settings)
    {
        var endpoint = (SelectedProvider?.Provider as IConnectionSettingsProvider)?.GetEndpoint(settings);
        var field = Fields.FirstOrDefault(static f => f.IsFilePath && f.Value.Length > 0);
        var place = field is not null
            ? Path.GetFileName(field.Value)
            : endpoint?.Host ?? Fields.FirstOrDefault(static f => f.IsRequired && f.Value.Length > 0)?.Value ?? string.Empty;
        // The first optional text setting that has a value: the database, service name… of the provider.
        var database = Fields.FirstOrDefault(static f => !f.IsRequired && f.Descriptor.Kind == ConnectionSettingKind.Text && f.Value.Length > 0)?.Value;
        return database is null || place.Length == 0 ? place : place + " / " + database;
    }

    /// <summary>Raised when the dialog should close; the argument tells whether the connection was saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    public string Title => _existing is null ? Strings.ConnectionDialogTitleNew : Strings.ConnectionDialogTitleEdit;

    public IReadOnlyList<ProviderOption> Providers { get; }

    public IReadOnlyList<EnvironmentOption> Environments { get; }

    public ObservableCollection<SettingFieldViewModel> Fields { get; } = [];

    /// <summary>False on systems without a protected store: saving a password would only last for the session.</summary>
    public bool CanPersistPasswords => _credentials.IsPersistent;

    /// <summary>The password box of an existing connection is empty; this hint explains that empty means "unchanged".</summary>
    public string? PasswordHint => _existing is { SavePassword: true } ? Strings.PasswordUnchangedHint : null;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial ProviderOption? SelectedProvider { get; set; }

    [ObservableProperty]
    public partial string Folder { get; set; }

    [ObservableProperty]
    public partial EnvironmentOption SelectedEnvironment { get; set; }

    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    [ObservableProperty]
    public partial string UserName { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; }

    [ObservableProperty]
    public partial bool SavePassword { get; set; }

    /// <summary>Whether the chosen authentication needs a user name and password.</summary>
    [ObservableProperty]
    public partial bool NeedsCredential { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(StopTestCommand), nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(CanChangeType))]
    public partial bool IsTesting { get; set; }

    [ObservableProperty]
    public partial string TestMessage { get; set; }

    /// <summary>Null until a test has finished; then whether it succeeded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestFailed))]
    public partial bool? TestSucceeded { get; set; }

    public bool TestFailed => TestSucceeded == false;

    [ObservableProperty]
    public partial string ValidationMessage { get; set; }

    /// <summary>Text for each outcome of a connection attempt.</summary>
    public static string Describe(ConnectionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var (format, argument) = attempt.Outcome switch
        {
            ConnectionOutcome.Succeeded => (Strings.OutcomeSucceeded, (object)attempt.Elapsed.TotalMilliseconds),
            ConnectionOutcome.ProviderMissing => (Strings.OutcomeProviderMissing, attempt.Detail ?? string.Empty),
            ConnectionOutcome.PasswordRequired => (Strings.OutcomePasswordRequired, string.Empty),
            ConnectionOutcome.Unreachable => (Strings.OutcomeUnreachable, attempt.Detail ?? string.Empty),
            _ => (Strings.OutcomeFailed, attempt.Detail ?? string.Empty),
        };
        return string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), argument);
    }

    /// <summary>The definition as currently entered. Never contains the password.</summary>
    public ConnectionDefinition BuildDefinition() => new()
    {
        Id = _existing?.Id ?? Guid.NewGuid(),
        Name = Name.Trim(),
        ProviderId = SelectedProvider?.Provider.Id ?? string.Empty,
        Settings = CurrentSettings(),
        Folder = string.Join('/', Folder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        Environment = SelectedEnvironment.Value,
        IsReadOnly = IsReadOnly,
        UserName = NeedsCredential && UserName.Trim() is { Length: > 0 } user ? user : null,
        SavePassword = NeedsCredential && SavePassword,
    };

    partial void OnSelectedProviderChanged(ProviderOption? value)
    {
        ApplyConnectionStringCommand.NotifyCanExecuteChanged();

        // A type chosen any other way (the paused Avalonia dialog's list) also ends the first step.
        if (value is not null)
        {
            IsChoosingType = false;
        }
        foreach (var field in Fields)
        {
            field.PropertyChanged -= OnFieldChanged;
        }

        Fields.Clear();
        if (value?.Provider is IConnectionSettingsProvider described)
        {
            // Values of the connection being edited apply only to its own provider.
            var saved = _existing?.ProviderId == value.Provider.Id ? _existing.Settings : null;
            foreach (var descriptor in described.ConnectionSettings)
            {
                var field = new SettingFieldViewModel(descriptor, saved?.GetValueOrDefault(descriptor.Key));
                field.PropertyChanged += OnFieldChanged;
                Fields.Add(field);
            }
        }

        UpdateNeedsCredential();
        OnPropertyChanged(nameof(FieldColumns));
        OnPropertyChanged(nameof(HasAdvancedFields));
        ShowAdvanced = Fields.Any(static f => f.IsAdvanced && f.IsChanged);
    }

    /// <summary>Columns of the main fields: two, or one when a file path needs the width.</summary>
    public int FieldColumns => Fields.Any(static f => f.IsFilePath) ? 1 : 2;

    /// <summary>Whether the type has fields under "Advanced".</summary>
    public bool HasAdvancedFields => Fields.Any(static f => f.IsAdvanced);

    /// <summary>Whether "Advanced" is open (it opens by itself when one of its fields is not at its default).</summary>
    [ObservableProperty]
    public partial bool ShowAdvanced { get; set; }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingFieldViewModel.Value))
        {
            UpdateNeedsCredential();
        }
    }

    private void UpdateNeedsCredential() =>
        NeedsCredential = SelectedProvider is not null &&
                          ((SelectedProvider.Provider as IConnectionSettingsProvider)?.RequiresCredential(CurrentSettings()) ?? true);

    private Dictionary<string, string> CurrentSettings() =>
        Fields.Where(static f => !string.IsNullOrWhiteSpace(f.Value)).ToDictionary(static f => f.Key, static f => f.Value.Trim(), StringComparer.Ordinal);

    private bool Validate()
    {
        ValidationMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationMessage = Strings.ValidationNameRequired;
        }
        else if (SelectedProvider is null)
        {
            ValidationMessage = Strings.ValidationProviderRequired;
        }
        else if (Fields.FirstOrDefault(static f => f.IsRequired && string.IsNullOrWhiteSpace(f.Value)) is { } missing)
        {
            ValidationMessage = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ValidationSettingRequired), missing.Label);
        }
        else if (NeedsCredential && string.IsNullOrWhiteSpace(UserName))
        {
            ValidationMessage = Strings.ValidationUserRequired;
        }

        return ValidationMessage.Length == 0;
    }

    private bool CanStartTest() => !IsTesting && !IsChoosingType;

    [RelayCommand(CanExecute = nameof(CanStartTest))]
    private async Task TestAsync()
    {
        if (!Validate())
        {
            return;
        }

        var definition = BuildDefinition();

        // An empty box means "use the saved password" when editing a connection that has one.
        var password = Password.Length > 0 || _existing is not { SavePassword: true } ? Password : null;
        IsTesting = true;
        TestSucceeded = null;
        TestMessage = Strings.ConnectionTesting;
        try
        {
            var job = _jobs.Enqueue("Test connection", context => _manager.TestAsync(definition, password, context.CancellationToken), JobPriority.High);
            _test = job;
            var attempt = await job.Completion;
            TestSucceeded = attempt.Outcome == ConnectionOutcome.Succeeded;
            TestMessage = Describe(attempt);
        }
        catch (OperationCanceledException)
        {
            TestMessage = Strings.OutcomeCanceled;
        }
        finally
        {
            _test = null;
            IsTesting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsTesting))]
    private void StopTest() => _test?.Cancel();

    [RelayCommand(CanExecute = nameof(CanStartTest))]
    private async Task SaveAsync()
    {
        if (!Validate())
        {
            return;
        }

        var definition = BuildDefinition();
        var password = Password;
        var job = _jobs.Enqueue(
            "Save connection",
            async context =>
            {
                await _store.SaveAsync(definition, context.CancellationToken).ConfigureAwait(false);
                if (!definition.SavePassword)
                {
                    await _credentials.DeletePasswordAsync(definition.Id, context.CancellationToken).ConfigureAwait(false);
                }
                else if (password.Length > 0 || _existing is not { SavePassword: true })
                {
                    await _credentials.SetPasswordAsync(definition.Id, password, context.CancellationToken).ConfigureAwait(false);
                }
            },
            JobPriority.High);
        await job.Completion;

        // The password has been handed to the credential store; do not keep it in the view model.
        Password = string.Empty;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _test?.Cancel();
        Password = string.Empty;
        ConnectionString = string.Empty;
        CloseRequested?.Invoke(this, false);
    }
}
