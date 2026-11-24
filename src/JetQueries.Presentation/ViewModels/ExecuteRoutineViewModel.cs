using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A way of passing an argument, as the Execute dialog lists it.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Text">Its name in the user's language.</param>
public sealed record RoutineArgumentOption(RoutineArgumentKind Kind, string Text);

/// <summary>What the user chose in the Execute dialog.</summary>
public enum RoutineExecutionChoice
{
    /// <summary>Nothing.</summary>
    Cancel,

    /// <summary>Open the script in a query tab.</summary>
    OpenScript,

    /// <summary>Open the script in a query tab and run it.</summary>
    Run,
}

/// <summary>One parameter of the routine: how it is passed and with what.</summary>
public sealed partial class RoutineParameterViewModel : ObservableObject
{
    public RoutineParameterViewModel(SqlParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        Parameter = parameter;
        IsOutput = parameter.Direction is "OUT" or "IN OUT";
        Options =
        [
            new(RoutineArgumentKind.Value, Strings.ExecuteModeValue),
            new(RoutineArgumentKind.Expression, Strings.ExecuteModeExpression),
            new(RoutineArgumentKind.Null, "NULL"),
            .. parameter.IsOptional ? [new RoutineArgumentOption(RoutineArgumentKind.Default, Strings.ExecuteModeDefault)] : Array.Empty<RoutineArgumentOption>(),
        ];

        // An optional parameter keeps its default; the others start as NULL until something is typed.
        SelectedOption = Options[parameter.IsOptional ? ^1 : 2];
    }

    public SqlParameterInfo Parameter { get; }

    public string Name => Parameter.Name;

    public string DataType => Parameter.DataType ?? string.Empty;

    /// <summary>IN, OUT or IN OUT.</summary>
    public string Direction => Parameter.Direction ?? "IN";

    /// <summary>An OUT or IN OUT parameter: its value is shown after the call.</summary>
    public bool IsOutput { get; }

    /// <summary>A value can be typed (not for a pure OUT parameter).</summary>
    public bool CanTakeValue => Parameter.Direction != "OUT";

    public IReadOnlyList<RoutineArgumentOption> Options { get; }

    [ObservableProperty]
    public partial RoutineArgumentOption SelectedOption { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    // Typing a value means passing it.
    partial void OnTextChanged(string value)
    {
        if (value.Length > 0 && SelectedOption.Kind is RoutineArgumentKind.Null or RoutineArgumentKind.Default)
        {
            SelectedOption = Options[0];
        }
    }

    public RoutineArgument ToArgument() => new(Parameter.Name, SelectedOption.Kind, Text);
}

/// <summary>
/// The Execute dialog of a stored procedure or function (SSMS's "Execute Stored Procedure"): the routine's
/// parameters, read from the catalog, each with a value, an expression, NULL or its default; the script the
/// provider writes for the call, kept up to date; and the choice to open it or run it in a query tab.
/// </summary>
public sealed partial class ExecuteRoutineViewModel : ObservableObject
{
    private readonly ConnectionSession _session;
    private readonly IMetadataService _metadata;
    private readonly IJobQueue _jobs;

    public ExecuteRoutineViewModel(ConnectionSession session, DatabaseObject target, IMetadataService metadata, IJobQueue jobs)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        _session = session;
        _metadata = metadata;
        _jobs = jobs;
        Target = target;
        Mark = ProviderMark.For(session.Provider);
    }

    /// <summary>Whether a routine of this connection can be run from the dialog.</summary>
    public static bool CanExecute(ConnectionSession session, DatabaseObject target) =>
        session?.Provider is IRoutineExecutionScripting && target?.Kind is DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function;

    public DatabaseObject Target { get; }

    public ProviderMark Mark { get; }

    /// <summary>schema.[package.]name.</summary>
    public string Title => string.Join('.', new[] { Target.Name.Schema, Target.Name.Container, Target.Name.Name }.Where(static p => !string.IsNullOrEmpty(p)));

    /// <summary>Kind · connection · database.</summary>
    public string Subtitle => string.Join(" · ", new[] { ObjectDetailsViewModel.KindName(Target.Kind), _session.Definition.Name, Target.Name.Database }.Where(static p => !string.IsNullOrEmpty(p)));

    /// <summary>The forms of the routine (Oracle and PostgreSQL overloads); usually one.</summary>
    public ObservableCollection<SqlSignature> Overloads { get; } = [];

    public bool HasOverloads => Overloads.Count > 1;

    [ObservableProperty]
    public partial SqlSignature? SelectedOverload { get; set; }

    public ObservableCollection<RoutineParameterViewModel> Parameters { get; } = [];

    public bool HasNoParameters => !IsLoading && Parameters.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoParameters))]
    public partial bool IsLoading { get; set; }

    /// <summary>Why the parameters could not be read (the call can still be written without them).</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The script for the call, as it stands.</summary>
    [ObservableProperty]
    public partial string Script { get; set; } = string.Empty;

    /// <summary>The provider's language service, for colouring the script.</summary>
    public ISqlLanguageService? LanguageService => _session.Provider.LanguageService;

    /// <summary>Reads the routine's signatures (one per overload) in the background.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        IReadOnlyList<SqlSignature> signatures;
        try
        {
            var target = Target;
            signatures = await _jobs.Enqueue(
                "Read parameters",
                context => new SessionMetadataProvider(_session, _metadata, target.Name.Database).GetSignaturesAsync(target, context.CancellationToken),
                JobPriority.High).Completion;
        }
        catch (OperationCanceledException)
        {
            signatures = [];
        }
#pragma warning disable CA1031 // The dialog says why; the routine can still be called without parameters.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = string.Format(CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.ExecuteParametersFailed), ex.Message);
            signatures = [];
        }

        Overloads.Clear();
        foreach (var signature in signatures.DefaultIfEmpty(new SqlSignature(Target.Name.Name, [], null)))
        {
            Overloads.Add(signature);
        }

        OnPropertyChanged(nameof(HasOverloads));
        IsLoading = false;
        SelectedOverload = Overloads[0];
    }

    partial void OnSelectedOverloadChanged(SqlSignature? value)
    {
        foreach (var parameter in Parameters)
        {
            parameter.PropertyChanged -= OnParameterChanged;
        }

        Parameters.Clear();
        foreach (var info in value?.Parameters ?? [])
        {
            var parameter = new RoutineParameterViewModel(info);
            parameter.PropertyChanged += OnParameterChanged;
            Parameters.Add(parameter);
        }

        OnPropertyChanged(nameof(HasNoParameters));
        UpdateScript();
    }

    private void OnParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateScript();

    // The provider writes the call; it is pure and quick, so it follows every keystroke.
    private void UpdateScript() =>
        Script = SelectedOverload is { } signature && _session.Provider is IRoutineExecutionScripting scripting
            ? scripting.ScriptExecute(Target, signature, [.. Parameters.Select(static p => p.ToArgument())]) ?? string.Empty
            : string.Empty;

    /// <summary>The query tab to open: the script, on the routine's connection and database.</summary>
    public NewQueryRequest Request(bool run) =>
        new(_session.Definition.Id, ConnectionsViewModel.DatabaseOf(_session, Target)) { Text = Script, Title = Target.Name.Name, Run = run };
}
