using System.Globalization;

namespace JetQueries.Presentation.Tests;

/// <summary>Every DBA screen explanation is complete in both languages.</summary>
public sealed class DbaHelpTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void Every_topic_has_a_title_an_explanation_and_what_to_do(string language)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        foreach (var topic in DbaHelp.All)
        {
            var help = DbaHelp.Get(topic);
            Assert.True(help is not null, $"{topic} has no text in {language}");
            Assert.True(help.What.Length > 60, $"{topic} explains too little in {language}");
            Assert.True(help.Action.Length > 30, $"{topic} says too little about what to do in {language}");
        }

        Assert.Null(DbaHelp.Get("NoSuchTopic"));
    }

    [Fact]
    public void Both_languages_have_the_same_levels_and_Turkish_is_really_Turkish()
    {
        foreach (var topic in DbaHelp.All)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            var english = DbaHelp.Get(topic)!;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            var turkish = DbaHelp.Get(topic)!;
            Assert.Equal(english.Good is null, turkish.Good is null);
            Assert.Equal(english.Watch is null, turkish.Watch is null);
            Assert.Equal(english.Bad is null, turkish.Bad is null);
            Assert.NotEqual(english.What, turkish.What);
        }

        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
        var ple = DbaHelp.Get(DbaHelp.DashPle)!;
        Assert.Equal("Sayfa ömrü (PLE)", ple.Title);
        Assert.True(ple.HasLevels);
        Assert.False(DbaHelp.Get(DbaHelp.ActKill)!.Good is not null);
    }
}
