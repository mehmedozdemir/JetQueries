using Avalonia.Threading;
using JetQueries.Presentation.Services;

namespace JetQueries.Desktop.Services;

/// <summary>The Avalonia UI dispatcher.</summary>
internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
