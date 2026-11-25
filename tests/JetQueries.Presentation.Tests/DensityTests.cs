using JetQueries.Presentation.ViewModels;

namespace JetQueries.Presentation.Tests;

/// <summary>The interface density on five levels; settings saved with the old compact switch keep their look.</summary>
public sealed class DensityTests
{
    [Theory]
    [InlineData(null, false, 0)]
    [InlineData(null, true, -1)]
    [InlineData(2, true, 2)]
    [InlineData(-5, false, -2)]
    [InlineData(9, false, 2)]
    public void The_level_in_effect_comes_from_the_level_or_the_old_switch(int? level, bool compact, int expected) =>
        Assert.Equal(expected, new AppearanceSettings { DensityLevel = level, CompactMode = compact }.EffectiveDensity);
}
