using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace JetQueries.Desktop.ViewModels;

/// <summary>A tab in a query's results area.</summary>
public interface IResultTab
{
    /// <summary>Caption of the tab.</summary>
    string Title { get; }
}

/// <summary>
/// Chooses the content of a results tab by the kind of tab: the template keyed <c>grid</c> for a result set,
/// the one keyed <c>messages</c> for the messages.
/// </summary>
public sealed class ResultTabTemplate : IDataTemplate
{
    /// <summary>The templates, keyed <c>grid</c> and <c>messages</c>.</summary>
    [Content]
    public Dictionary<string, IDataTemplate> Templates { get; } = [];

    public bool Match(object? data) => data is IResultTab;

    public Control? Build(object? param) => Templates[param is QueryResultSet ? "grid" : "messages"].Build(param);
}
