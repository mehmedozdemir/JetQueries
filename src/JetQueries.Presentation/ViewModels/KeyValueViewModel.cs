using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>An item of a collection as listed.</summary>
public sealed class KeyItemViewModel(KeyValueItem item)
{
    public KeyValueItem Item { get; } = item;

    public string Name => Item.Name ?? string.Empty;

    /// <summary>The value on one line, cut short.</summary>
    public string Preview { get; } = Shorten(item.Value);

    public string Score => Item.Score?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Shorten(string value)
    {
        var line = value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}

/// <summary>
/// A key of a key-value database in a tab (RedisInsight's key view): its type, time to live and size; a string's
/// text, or a collection's items with an editor for the selected one. Every change is one command, sent at once;
/// a read-only connection only reads, a production connection asks before deleting.
/// </summary>
public sealed partial class KeyValueViewModel : ObservableObject
{
    /// <summary>The most items of a collection read.</summary>
    public const int MaxItems = 1000;

    private readonly ConnectionSession _session;
    private readonly IJobQueue _jobs;
    private readonly IDialogService _dialogs;

    public KeyValueViewModel(ConnectionSession session, int database, string key, IJobQueue jobs, IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(key);
        _session = session;
        _jobs = jobs;
        _dialogs = dialogs;
        Database = database;
        Key = key;
        Text = string.Empty;
        EditName = string.Empty;
        EditValue = string.Empty;
        EditScore = string.Empty;
    }

    public Guid ConnectionId => _session.Definition.Id;

    public int Database { get; }

    public ConnectionEnvironment Environment => _session.Definition.Environment;

    /// <summary>The connection only reads.</summary>
    public bool IsReadOnly => _session.Definition.IsReadOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText), nameof(ToolTip))]
    public partial string Key { get; private set; }

    /// <summary>The tab's title: the key (its end when long).</summary>
    public string HeaderText => Key.Length > 40 ? "…" + Key[^39..] : Key;

    public string ToolTip => Key + "\n" + _session.Definition.Name + " · db" + Database.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Kartlar · db0".</summary>
    public string Location => _session.Definition.Name + " · db" + Database.ToString(CultureInfo.InvariantCulture);

    /// <summary>What was read; null before, or when the key is gone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeText), nameof(TtlText), nameof(SizeText), nameof(IsText), nameof(IsCollection), nameof(HasNames), nameof(HasScores),
        nameof(NameHeader), nameof(ValueHeader), nameof(Exists), nameof(TruncatedText), nameof(CanEdit), nameof(CanEditNames))]
    [NotifyCanExecuteChangedFor(nameof(SaveTextCommand), nameof(AddItemCommand), nameof(SaveItemCommand), nameof(RemoveItemCommand), nameof(RenameCommand),
        nameof(SetTimeToLiveCommand), nameof(DeleteCommand))]
    public partial KeyValue? Value { get; private set; }

    public bool Exists => Value is not null;

    public bool CanEdit => Exists && !IsReadOnly && Value!.Type != KeyValueType.Other;

    public string TypeText => Value?.Type switch
    {
        KeyValueType.Text => "string",
        KeyValueType.Hash => "hash",
        KeyValueType.List => "list",
        KeyValueType.Set => "set",
        KeyValueType.SortedSet => "zset",
        KeyValueType.Stream => "stream",
        null => string.Empty,
        _ => Value.Text ?? "?",
    };

    /// <summary>"TTL: 9 dk 58 sn", or "TTL: yok".</summary>
    public string TtlText => Value?.TimeToLive is { } ttl ? Strings.KeyTtl + " " + Duration(ttl) : Strings.KeyTtl + " " + Strings.KeyNoTtl;

    /// <summary>"12 öğe · 1,2 KB".</summary>
    public string SizeText => Value is not { } value ? string.Empty
        : (value.Type == KeyValueType.Text
            ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.KeyCharacters), value.Length)
            : string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.KeyItems), value.Length))
          + (value.MemoryBytes is { } bytes ? " · " + Bytes(bytes) : string.Empty);

    /// <summary>"İlk 1 000 / 25 000 öğe gösteriliyor" when not every item was read.</summary>
    public string? TruncatedText => Value is { IsTruncated: true } value
        ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.KeyTruncated), value.Items.Count, value.Length)
        : null;

    public bool IsText => Value?.Type == KeyValueType.Text;

    public bool IsCollection => Value?.Type is KeyValueType.Hash or KeyValueType.List or KeyValueType.Set or KeyValueType.SortedSet or KeyValueType.Stream;

    /// <summary>Items have names: hash fields, list indexes, stream ids.</summary>
    public bool HasNames => Value?.Type is KeyValueType.Hash or KeyValueType.List or KeyValueType.Stream;

    /// <summary>A hash field's name can be typed (a list's index and a stream's id cannot).</summary>
    public bool CanEditNames => CanEdit && Value?.Type == KeyValueType.Hash;

    public bool HasScores => Value?.Type == KeyValueType.SortedSet;

    public string NameHeader => Value?.Type switch
    {
        KeyValueType.Hash => Strings.KeyField,
        KeyValueType.List => Strings.KeyIndex,
        KeyValueType.Stream => "ID",
        _ => string.Empty,
    };

    public string ValueHeader => Value?.Type switch
    {
        KeyValueType.Set or KeyValueType.SortedSet => Strings.KeyMember,
        KeyValueType.Stream => Strings.KeyFields,
        _ => Strings.KeyValueColumn,
    };

    public ObservableCollection<KeyItemViewModel> Items { get; } = [];

    /// <summary>The selected item, shown in the editor.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveItemCommand), nameof(RemoveItemCommand))]
    public partial KeyItemViewModel? SelectedItem { get; set; }

    /// <summary>A string's text.</summary>
    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; }

    [ObservableProperty]
    public partial string EditValue { get; set; }

    [ObservableProperty]
    public partial string EditScore { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>What was done, or why not.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool Failed { get; set; }

    partial void OnSelectedItemChanged(KeyItemViewModel? value)
    {
        EditName = value?.Item.Name ?? string.Empty;
        EditValue = value?.Item.Value ?? string.Empty;
        EditScore = value?.Score ?? string.Empty;
    }

    /// <summary>Reads the key again.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var (database, key) = (Database, Key);
        IsBusy = true;
        try
        {
            var value = await _jobs.Enqueue(
                "Read key",
                job => _session.UseMetadataAsync((metadata, token) => Store(metadata).GetAsync(database, key, MaxItems, token), job.CancellationToken),
                JobPriority.High).Completion;
            var selected = SelectedItem?.Item;
            Value = value;
            Items.Clear();
            foreach (var item in value?.Items ?? [])
            {
                Items.Add(new KeyItemViewModel(item));
            }

            Text = value?.Text ?? string.Empty;
            SelectedItem = Items.FirstOrDefault(i => i.Item.Name == selected?.Name && (selected?.Name is not null || i.Item.Value == selected?.Value)) ?? Items.FirstOrDefault();
            if (value is null)
            {
                Report(Strings.KeyGone, failed: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // The server's reason is shown in the tab.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Report(ex.Message, failed: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanChange() => CanEdit && !IsBusy;

    private bool CanChangeItem() => CanChange() && SelectedItem is not null;

    /// <summary>Saves a string's text (its time to live stays).</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task SaveTextAsync() => await ChangeAsync(new KeyChange(Key, KeyChangeKind.SetText) { Text = Text }, Strings.KeySaved);

    /// <summary>Adds the item in the editor (a hash field is set, a list gets it at its end).</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task AddItemAsync() => EditedItem() is { } item
        ? ChangeAsync(new KeyChange(Key, KeyChangeKind.AddItem, Value!.Type) { Item = item }, Strings.KeyItemAdded)
        : Task.CompletedTask;

    /// <summary>Replaces the selected item with the one in the editor.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeItem))]
    private Task SaveItemAsync() => EditedItem(keepName: Value!.Type is KeyValueType.List) is { } item
        ? ChangeAsync(new KeyChange(Key, KeyChangeKind.ReplaceItem, Value.Type) { OldItem = SelectedItem!.Item, Item = item }, Strings.KeySaved)
        : Task.CompletedTask;

    /// <summary>Removes the selected item (a production connection asks first).</summary>
    [RelayCommand(CanExecute = nameof(CanChangeItem))]
    private async Task RemoveItemAsync()
    {
        var item = SelectedItem!.Item;
        if (Environment == ConnectionEnvironment.Production &&
            !await _dialogs.ConfirmAsync(Strings.KeyRemoveItemTitle, Format(Strings.KeyRemoveItemProduction, item.Name ?? item.Value, Key, _session.Definition.Name)))
        {
            return;
        }

        await ChangeAsync(new KeyChange(Key, KeyChangeKind.RemoveItem, Value!.Type) { OldItem = item }, Strings.KeyItemRemoved);
    }

    /// <summary>Renames the key (refused when the new name is taken).</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task RenameAsync()
    {
        if (await _dialogs.PromptTextAsync(Strings.KeyRename, Strings.KeyRenamePrompt, Key) is not { Length: > 0 } name || name == Key)
        {
            return;
        }

        if (await ChangeAsync(new KeyChange(Key, KeyChangeKind.Rename) { NewKey = name }, Strings.KeyRenamed, refresh: false))
        {
            Key = name;
            await RefreshAsync();
        }
    }

    /// <summary>Sets the time to live in seconds; empty keeps the key for ever.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task SetTimeToLiveAsync()
    {
        var current = Value?.TimeToLive is { } ttl ? ((long)ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture) : string.Empty;
        if (await _dialogs.PromptTextAsync(Strings.KeySetTtl, Strings.KeySetTtlPrompt, current) is not { } text)
        {
            return;
        }

        TimeSpan? seconds = null;
        if (text.Trim().Length > 0)
        {
            if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            {
                Report(Strings.KeyBadTtl, failed: true);
                return;
            }

            seconds = TimeSpan.FromSeconds(number);
        }

        await ChangeAsync(new KeyChange(Key, KeyChangeKind.Expire) { TimeToLive = seconds }, Strings.KeySaved);
    }

    /// <summary>Deletes the key after asking.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task DeleteAsync()
    {
        var question = Environment == ConnectionEnvironment.Production
            ? Format(Strings.KeyDeleteProduction, Key, _session.Definition.Name)
            : Format(Strings.KeyDeleteMessage, Key);
        if (!await _dialogs.ConfirmAsync(Strings.KeyDeleteTitle, question))
        {
            return;
        }

        if (await ChangeAsync(new KeyChange(Key, KeyChangeKind.Delete), Strings.KeyDeleted, refresh: false))
        {
            Value = null;
            Items.Clear();
            Text = string.Empty;
        }
    }

    /// <summary>Indents a string that holds JSON (not saved until Save).</summary>
    [RelayCommand]
    private void FormatJson()
    {
        try
        {
            using var document = JsonDocument.Parse(Text);
            Text = JsonSerializer.Serialize(document.RootElement, IndentedJson);
            Report(null, failed: false);
        }
        catch (JsonException)
        {
            Report(Strings.KeyNotJson, failed: true);
        }
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private KeyValueItem? EditedItem(bool keepName = false)
    {
        double? score = null;
        if (HasScores)
        {
            if (!double.TryParse(EditScore.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                !double.TryParse(EditScore.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out number))
            {
                Report(Strings.KeyBadScore, failed: true);
                return null;
            }

            score = number;
        }

        if (Value!.Type == KeyValueType.Hash && EditName.Length == 0)
        {
            Report(Strings.KeyNeedsField, failed: true);
            return null;
        }

        var name = Value.Type == KeyValueType.Hash ? EditName : keepName ? SelectedItem?.Item.Name : null;
        return new KeyValueItem(name, EditValue, score);
    }

    private async Task<bool> ChangeAsync(KeyChange change, string done, bool refresh = true)
    {
        // Below the buttons too: a read-only connection never writes, whatever asks.
        if (IsReadOnly)
        {
            Report(Strings.QueryBlockedReadOnly, failed: true);
            return false;
        }

        var database = Database;
        IsBusy = true;
        bool applied;
        try
        {
            applied = await _jobs.Enqueue(
                "Change key",
                job => _session.UseMetadataAsync((metadata, token) => Store(metadata).ChangeAsync(database, change, token), job.CancellationToken),
                JobPriority.High).Completion;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
#pragma warning disable CA1031 // The server's reason is shown in the tab.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Report(ex.Message, failed: true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }

        Report(applied ? done : Strings.KeyNotApplied, failed: !applied);
        if (refresh)
        {
            await RefreshAsync();
            Report(applied ? done : Strings.KeyNotApplied, failed: !applied);
        }

        return applied;
    }

    private void Report(string? text, bool failed)
    {
        Status = text;
        Failed = failed;
    }

    private static IKeyValueStore Store(IMetadataProvider metadata) =>
        metadata as IKeyValueStore ?? throw new NotSupportedException("This connection has no keys.");

    private static string Format(string format, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), arguments);

    private static string Duration(TimeSpan time) => time.TotalDays >= 1 ? Format(Strings.KeyDays, (int)time.TotalDays, time.Hours)
        : time.TotalHours >= 1 ? Format(Strings.KeyHours, (int)time.TotalHours, time.Minutes)
        : time.TotalMinutes >= 1 ? Format(Strings.KeyMinutes, (int)time.TotalMinutes, time.Seconds)
        : Format(Strings.KeySeconds, Math.Max(0, (int)time.TotalSeconds));

    private static string Bytes(long bytes) => bytes >= 1024 * 1024
        ? string.Create(CultureInfo.CurrentCulture, $"{bytes / 1048576.0:0.#} MB")
        : bytes >= 1024 ? string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.#} KB") : string.Create(CultureInfo.CurrentCulture, $"{bytes} B");
}
