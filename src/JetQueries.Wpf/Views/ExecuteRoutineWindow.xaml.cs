using System.Globalization;
using System.Windows;
using System.Windows.Data;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;

namespace JetQueries.Wpf.Views;

/// <summary>
/// The Execute dialog of a stored procedure or function: its parameters with a value, an expression, NULL or the
/// default each, and the script that will run, kept up to date. "Open script" puts it in a query tab, "Run" runs it
/// there too.
/// </summary>
public partial class ExecuteRoutineWindow : Window
{
    public ExecuteRoutineWindow(ExecuteRoutineViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>What the user chose.</summary>
    public RoutineExecutionChoice Choice { get; private set; }

    private void OnOpenScript(object sender, RoutedEventArgs e) => Close(RoutineExecutionChoice.OpenScript);

    private void OnRun(object sender, RoutedEventArgs e) => Close(RoutineExecutionChoice.Run);

    private void Close(RoutineExecutionChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}

/// <summary>An overload as "NAME(p1 type, p2 type) → returns", for the list of forms.</summary>
public sealed class SignatureTextConverter : IValueConverter
{
    public static SignatureTextConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is SqlSignature signature
        ? signature.Name + "(" + string.Join(", ", signature.Parameters.Select(static p => p.DataType is { } type ? p.Name + " " + type : p.Name)) + ")"
          + (signature.ReturnType is { } returns ? " → " + returns : string.Empty)
        : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
