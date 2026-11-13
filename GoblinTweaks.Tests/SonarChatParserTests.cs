namespace GoblinTweaks.Tests;

public class SonarChatParserTests
{
    [Fact]
    public void HuntFound()
        => Assert.Equal(new SonarChatMessage(HuntKind.A, "Rusalka", "Jenova", 0, null, false),
            SonarChatParser.Parse("Rank A: Rusalka ", " <Jenova>"));

    [Fact]
    public void HuntKilled()
    {
        var m = SonarChatParser.Parse("Rank S: Bone Crawler ", " <Jenova> was just killed");
        Assert.NotNull(m);
        Assert.True(m.Dead);
        Assert.Equal(HuntKind.S, m.Kind);
        Assert.Equal("Bone Crawler", m.Name);
    }

    [Fact]
    public void SsAndMinion_AreS()
    {
        Assert.Equal(HuntKind.S, SonarChatParser.Parse("Rank SS: Ker ", " <Jenova>")?.Kind);
        Assert.Equal(HuntKind.S, SonarChatParser.Parse("Rank SSMinion: Ker Shroud ", " <Jenova>")?.Kind);
    }

    [Fact]
    public void RankB_ReturnsNull()
    {
        Assert.Null(SonarChatParser.Parse("Rank B: Gnath Cometdrone ", " <Jenova>"));
        Assert.Null(SonarChatParser.Parse("Rank None: Thing ", " <Jenova>"));
    }

    [Fact]
    public void InstanceIcon_IsParsed()
        => Assert.Equal(2u, SonarChatParser.Parse("Rank A: Supay ", " <Jenova>")?.InstanceId);

    [Fact]
    public void CrossWorldIcon_IsNotPartOfTheWorld()
        => Assert.Equal("Gilgamesh", SonarChatParser.Parse("Rank S: Ruminator ", " <Gilgamesh>")?.WorldName);

    [Fact]
    public void FateRunning()
    {
        var m = SonarChatParser.Parse("FATE: Long Live the Coeurl ", " <Jenova> (45% with 12:30 remaining)");
        Assert.NotNull(m);
        Assert.Equal(HuntKind.Fate, m.Kind);
        Assert.Equal("Long Live the Coeurl", m.Name);
        Assert.Equal(45f, m.Percent);
    }

    [Fact]
    public void FatePreparation()
        => Assert.Equal(0f, SonarChatParser.Parse("FATE: Long Live the Coeurl ", " <Jenova> (in Preparation)")?.Percent);

    [Fact]
    public void LocalizedName_IsKeptAsIs()
        => Assert.Equal("ルサルカ", SonarChatParser.Parse("Rank A: ルサルカ ", " <Jenova>")?.Name);

    [Fact]
    public void NonRelayText_ReturnsNull()
    {
        Assert.Null(SonarChatParser.Parse("Connected to Sonar server", ""));
        Assert.Null(SonarChatParser.Parse("Rank A: Rusalka ", " no world"));
    }

    [Fact]
    public void BlankName_ReturnsNull()
    {
        Assert.Null(SonarChatParser.Parse("FATE:  ", " <Jenova>"));
        Assert.Null(SonarChatParser.Parse("Rank A:   ", " <Jenova>"));
    }
}
