namespace JetQueries.Wpf.Tests;

/// <summary>UiKit's sizes on the five density levels.</summary>
public sealed class DensityLevelTests
{
    [Theory]
    [InlineData(-2, 24.5)]
    [InlineData(-1, 28)]
    [InlineData(0, 34)]
    [InlineData(1, 38)]
    [InlineData(2, 42.5)]
    public void Sizes_step_from_compact_through_normal_to_spacious(int level, double expected) =>
        Assert.Equal(expected, UiKit.Theming.Density.Size(34, 28, level));
}
