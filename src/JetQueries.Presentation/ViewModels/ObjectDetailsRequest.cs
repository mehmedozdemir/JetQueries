using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A request to show the details view of an object.</summary>
/// <param name="ConnectionId">The connection the object is on.</param>
/// <param name="Target">The object.</param>
public sealed record ObjectDetailsRequest(Guid ConnectionId, DatabaseObject Target);
