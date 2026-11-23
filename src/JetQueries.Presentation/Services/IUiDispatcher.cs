namespace JetQueries.Presentation.Services;

/// <summary>
/// Hands work to the UI thread. View models that receive data from background jobs use this instead of a UI
/// framework's dispatcher directly, so they can be tested without one.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread, after everything queued before it.</summary>
    void Post(Action action);
}
