namespace JetQueries.Presentation.ViewModels;

/// <summary>What every tab of the document area has, whatever it shows (a query, an object's details, a DBA screen).</summary>
public interface IDocumentTab
{
    /// <summary>
    /// Pinned: kept at the front of the tab row, with a pin instead of the close button, and left open by
    /// "close others", "close all" and "close to the left/right". Closing it on its own still works.
    /// </summary>
    bool IsPinned { get; set; }

    /// <summary>The connection the tab works on, if any (for "close this connection's tabs").</summary>
    Guid? ConnectionId { get; }
}
