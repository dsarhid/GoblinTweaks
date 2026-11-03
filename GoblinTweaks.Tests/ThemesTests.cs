namespace GoblinTweaks.Tests;

public class ThemesTests
{
    private static IReadOnlyList<Theme> Load()
        => Themes.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "themes.json")));

    private static Theme Pirate() => Load().Single(theme => theme.Key == "pirate");

    [Fact]
    public void Parse_LoadsAtLeast15Themes()
    {
        var themes = Load();
        Assert.True(themes.Count >= 15, $"only {themes.Count} themes");
        Assert.Equal(themes.Count, themes.Select(theme => theme.Key).Distinct().Count());
        Assert.All(themes, theme => Assert.NotEmpty(theme.Keywords));
    }

    [Fact]
    public void Match_PirateMatchesCorsairCoat()
        => Assert.Equal(1f, Themes.Match(Pirate(), "Corsair's Coat"));

    [Fact]
    public void Match_IsWholeWord()
    {
        var samurai = Load().Single(theme => theme.Key == "samurai");
        Assert.Equal(1f, Themes.Match(samurai, "Kasa of the Wind"));
        Assert.Equal(0f, Themes.Match(samurai, "Kasabian Boots"));
    }

    [Fact]
    public void Match_AvoidWordWins()
    {
        var theme = new Theme("test", ["Coat"], ["Rubber"]);
        Assert.Equal(1f, Themes.Match(theme, "Leather Coat"));
        Assert.Equal(0f, Themes.Match(theme, "Rubber Coat"));
    }

    [Fact]
    public void MatchWords_IsCaseInsensitiveWholeWord()
    {
        Assert.Equal(1f, Themes.MatchWords(["coat"], "Corsair's Coat"));
        Assert.Equal(0f, Themes.MatchWords(["coa"], "Corsair's Coat"));
        Assert.Equal(0f, Themes.MatchWords([], "Corsair's Coat"));
    }

    [Fact]
    public void CanWear_RespectsRaceAndGender()
    {
        var races = new bool[8];
        races[7] = true; // Viera
        var vieraFemale = new RaceRule(37, races, Male: false, Female: true);

        Assert.True(Wearability.CanWear(vieraFemale, race: 8, male: false));
        Assert.False(Wearability.CanWear(vieraFemale, race: 8, male: true));
        Assert.False(Wearability.CanWear(vieraFemale, race: 1, male: false));

        var everyone = new RaceRule(1, new bool[8], Male: false, Female: false);
        Assert.True(Wearability.CanWear(everyone, race: 3, male: true));
        Assert.True(Wearability.CanWear(new RaceRule(0, new bool[8], false, false), race: 5, male: false));
    }
}
