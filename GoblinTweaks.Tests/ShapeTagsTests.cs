namespace GoblinTweaks.Tests;

public class ShapeTagsTests
{
    private static readonly ShapeTable Table = ShapeTags.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shapes.json")));

    [Fact]
    public void Parse_EveryShapeHasNamesAndWords()
    {
        foreach (var shape in Enum.GetValues<ShapeTag>().Where(shape => shape != ShapeTag.None))
        {
            var entry = Table.Shapes.SingleOrDefault(s => s.Shape == shape);
            Assert.True(entry is not null, $"{shape} is missing from shapes.json");
            Assert.NotEmpty(entry!.Names);
            Assert.NotEmpty(entry.Words);
        }
        Assert.NotEmpty(Table.Clashes);
    }

    [Theory]
    [InlineData("Darkvein Cape", ShapeTag.Cape)]
    [InlineData("Capelet of Fending", ShapeTag.Cape)]
    [InlineData("Escape Boots", ShapeTag.Boots)]                // "Escape" is not a cape
    [InlineData("Sabatons of Fending", ShapeTag.HeavyArmor)]
    [InlineData("Southern Seas Bikini Top", ShapeTag.Swimsuit)]
    [InlineData("Hooded Cloak of Shadows", ShapeTag.Cape)]      // "Hooded" is not "Hood"
    [InlineData("Plain Thing", ShapeTag.None)]
    public void FromName_MatchesWholeWords(string name, ShapeTag expected)
        => Assert.Equal(expected, ShapeTags.FromName(name, Table));

    [Fact]
    public void FromName_SeveralShapes()
        => Assert.Equal(ShapeTag.Cape | ShapeTag.Coat, ShapeTags.FromName("Cloaked Coat with Cape", Table));

    [Theory]
    [InlineData("quiero una capa y botas", ShapeTag.Cape | ShapeTag.Boots)]
    [InlineData("algo con CAPUCHA", ShapeTag.Hood)]
    [InlineData("Vestido largo", ShapeTag.Dress)]
    [InlineData("un traje de baño", ShapeTag.Swimsuit)]
    [InlineData("dark set with a cape", ShapeTag.Cape)]
    [InlineData("capas negras", ShapeTag.Cape)]                 // plural
    [InlineData("algo oscuro para DRK", ShapeTag.None)]
    public void FromRequest_SpanishAndEnglish(string request, ShapeTag expected)
        => Assert.Equal(expected, ShapeTags.FromRequest(request, Table));

    [Fact]
    public void Clash_EitherOrderAndCombinedTags()
    {
        var forward = ShapeTags.Clash(ShapeTag.HeavyArmor, ShapeTag.Swimsuit, Table.Clashes);
        Assert.True(forward > 0f);
        Assert.Equal(forward, ShapeTags.Clash(ShapeTag.Swimsuit, ShapeTag.HeavyArmor, Table.Clashes));
        Assert.Equal(forward, ShapeTags.Clash(ShapeTag.HeavyArmor | ShapeTag.Cape, ShapeTag.Swimsuit, Table.Clashes));
        Assert.Equal(0f, ShapeTags.Clash(ShapeTag.Cape, ShapeTag.Boots, Table.Clashes));
        Assert.Equal(0f, ShapeTags.Clash(ShapeTag.None, ShapeTag.Swimsuit, Table.Clashes));
    }

    [Fact]
    public void Describe_IsPlainEnglish()
    {
        Assert.Equal("cape, heavy armor", ShapeTags.Describe(ShapeTag.Cape | ShapeTag.HeavyArmor));
        Assert.Equal("-", ShapeTags.Describe(ShapeTag.None));
    }
}
