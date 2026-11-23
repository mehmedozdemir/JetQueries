using System.Windows;
using System.Windows.Threading;
using JetQueries.Wpf;

namespace JetQueries.Wpf.Tests;

/// <summary>
/// One WPF application for the whole test run, on its own STA thread with a running dispatcher. Tests hand
/// their UI work to it; async continuations come back to that thread, as they would in the application.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Task<Dispatcher>> Started = new(Start);

    public static async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var dispatcher = await Started.Value.ConfigureAwait(false);
        return await dispatcher.InvokeAsync(work).Task.Unwrap().ConfigureAwait(false);
    }

    public static Task RunAsync(Func<Task> work) => RunAsync(async () =>
    {
        await work();
        return true;
    });

    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // The real resources (UiKit tokens and styles, JetQueries additions); no host, no saved theme.
                var app = new App();
                app.InitializeComponent();

                // After InitializeComponent: App.xaml sets OnMainWindowClose, and the first test window to close
                // would end the application for every later test.
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                App.LocalizeGrid();
                ready.SetResult(Dispatcher.CurrentDispatcher);
            }
#pragma warning disable CA1031 // Reported to the waiting test instead of being lost on this thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ready.SetException(ex);
                return;
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
}

/// <summary>Tests that use the WPF application share its one UI thread and must not run at the same time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUi
{
    public const string Name = "WPF";
}
