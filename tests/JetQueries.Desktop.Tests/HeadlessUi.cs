namespace JetQueries.Desktop.Tests;

/// <summary>
/// Tests that start a headless Avalonia session share process-wide UI state (the dispatcher, the font
/// manager), so they must not run at the same time as each other.
/// </summary>
[CollectionDefinition(Collection, DisableParallelization = true)]
public sealed class HeadlessUi
{
    public const string Collection = "Headless UI";
}
