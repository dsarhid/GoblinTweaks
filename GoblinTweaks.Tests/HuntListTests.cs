namespace GoblinTweaks.Tests;

public class HuntListTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 20, 0, 0, DateTimeKind.Utc);

    private static HuntReport Chat(string name, HuntKind kind = HuntKind.A, uint world = 1, uint inst = 0, bool dead = false, float? pct = null)
        => new(kind, name, world, "Jenova", 100, 10, inst, 18.5f, 13.6f, pct, null, dead, null, HuntSources.Chat);

    private static HuntReport Mem(string name, float pct, int players, DateTime found, bool dead = false, HuntKind kind = HuntKind.A)
        => new(kind, name, 1, "Jenova", 100, 10, 0, 18.5f, 13.6f, pct, players, dead, found, HuntSources.Memory);

    [Fact]
    public void Upsert_NewReturnsEntry_RepeatReturnsNull()
    {
        var list = new HuntList();
        Assert.NotNull(list.Upsert(Chat("Rusalka"), T0));
        Assert.Null(list.Upsert(Chat("Rusalka"), T0.AddSeconds(5)));
        Assert.Single(list.Visible(HuntFilter.All));
    }

    [Fact]
    public void Visible_IsNewestFoundFirst()
    {
        var list = new HuntList();
        list.Upsert(Chat("Rusalka"), T0);
        list.Upsert(Chat("Baal"), T0.AddMinutes(1));
        Assert.Equal(["Baal", "Rusalka"], list.Visible(HuntFilter.All).Select(e => e.Name));

        list.Upsert(Chat("Rusalka", pct: 50), T0.AddMinutes(2));
        Assert.Equal(["Baal", "Rusalka"], list.Visible(HuntFilter.All).Select(e => e.Name));
    }

    [Fact]
    public void Filter_S_A_Fate()
    {
        Assert.True(HuntFilters.Accepts(HuntFilter.S, HuntKind.S));
        Assert.False(HuntFilters.Accepts(HuntFilter.S, HuntKind.A));
        Assert.True(HuntFilters.Accepts(HuntFilter.All, HuntKind.Fate));
        Assert.True(HuntFilters.Accepts(HuntFilter.Fate, HuntKind.Fate));
        Assert.False(HuntFilters.Accepts(HuntFilter.Fate, HuntKind.S));

        Assert.Equal(HuntFilter.S, HuntFilters.Next(HuntFilter.All));
        Assert.Equal(HuntFilter.A, HuntFilters.Next(HuntFilter.S));
        Assert.Equal(HuntFilter.Fate, HuntFilters.Next(HuntFilter.A));
        Assert.Equal(HuntFilter.All, HuntFilters.Next(HuntFilter.Fate));
        Assert.Equal("FATE", HuntFilters.Label(HuntFilter.Fate));
        Assert.Equal("All", HuntFilters.Label(HuntFilter.All));

        var list = new HuntList();
        list.Upsert(Chat("Rusalka"), T0);
        list.Upsert(Chat("Bone Crawler", HuntKind.S), T0);
        list.Upsert(Chat("Coeurl", HuntKind.Fate), T0);
        Assert.Equal(["Bone Crawler"], list.Visible(HuntFilter.S).Select(e => e.Name));
        Assert.Equal(3, list.Visible(HuntFilter.All).Count);
    }

    [Fact]
    public void SameHuntFromChatAndMemory_IsOneEntryAndNewOnce()
    {
        var list = new HuntList();
        Assert.NotNull(list.Upsert(Chat("Rusalka"), T0));
        var created = list.ReplaceMemory([Mem("rusalka", 62, 7, T0)], T0.AddSeconds(1), announce: true);
        Assert.Empty(created);

        var e = Assert.Single(list.Visible(HuntFilter.All));
        Assert.Equal(62, e.Percent);
        Assert.Equal(7, e.Players);
        Assert.Equal(HuntSources.Chat | HuntSources.Memory, e.Sources);
    }

    [Fact]
    public void SameNameDifferentWorldOrInstance_AreSeparate()
    {
        var list = new HuntList();
        list.Upsert(Chat("Supay", world: 1, inst: 1), T0);
        list.Upsert(Chat("Supay", world: 2, inst: 1), T0);
        list.Upsert(Chat("Supay", world: 1, inst: 2), T0);
        Assert.Equal(3, list.Visible(HuntFilter.All).Count);
    }

    [Fact]
    public void DeadHunt_StaysFiveMinutes()
    {
        var list = new HuntList();
        list.Upsert(Chat("Rusalka"), T0.AddMinutes(-10));
        list.Upsert(Chat("Rusalka", dead: true), T0);

        list.Expire(T0.AddMinutes(4).AddSeconds(59));
        var e = Assert.Single(list.Visible(HuntFilter.All));
        Assert.Equal(HuntRowState.Dead, HuntList.StateOf(e));

        list.Expire(T0.AddMinutes(5).AddSeconds(1));
        Assert.Empty(list.Visible(HuntFilter.All));
    }

    [Fact]
    public void ChatOnlyFate_ExpiresAfter30Minutes()
    {
        var list = new HuntList();
        list.Upsert(Chat("Coeurl", HuntKind.Fate, pct: 45), T0);
        list.Expire(T0.AddMinutes(29));
        Assert.Single(list.Visible(HuntFilter.All));
        list.Expire(T0.AddMinutes(31));
        Assert.Empty(list.Visible(HuntFilter.All));
    }

    [Fact]
    public void EndedFate_RemovedAtOnce()
    {
        var list = new HuntList();
        list.ReplaceMemory([Mem("Coeurl", 80, 12, T0, kind: HuntKind.Fate)], T0, announce: false);
        list.ReplaceMemory([Mem("Coeurl", 100, 12, T0, dead: true, kind: HuntKind.Fate)], T0.AddSeconds(1), announce: true);
        list.Expire(T0.AddSeconds(1));
        Assert.Empty(list.Visible(HuntFilter.All));
    }

    [Fact]
    public void ChatOnlyHunt_ExpiresAfterOneHourWithoutUpdate()
    {
        var list = new HuntList();
        list.Upsert(Chat("Rusalka"), T0);
        list.Expire(T0.AddMinutes(59));
        Assert.Single(list.Visible(HuntFilter.All));
        list.Expire(T0.AddMinutes(61));
        Assert.Empty(list.Visible(HuntFilter.All));
    }

    [Fact]
    public void ReplaceMemory_DropsEntriesSonarNoLongerShows()
    {
        var list = new HuntList();
        list.Upsert(Chat("Rusalka"), T0);
        list.ReplaceMemory([Mem("Rusalka", 100, 3, T0), Mem("Baal", 100, 2, T0)], T0, announce: false);
        Assert.Equal(2, list.Visible(HuntFilter.All).Count);

        // Sonar's view is the authority while it can be read: what it drops goes, even if the chat announced it.
        list.Upsert(Chat("Supay"), T0.AddSeconds(1));
        list.ReplaceMemory([], T0.AddSeconds(1), announce: true);
        var e = Assert.Single(list.Visible(HuntFilter.All));
        Assert.Equal("Supay", e.Name);   // chat-only, Sonar's memory has not shown it yet
        Assert.Equal(HuntSources.Chat, e.Sources);
    }

    [Fact]
    public void MemoryRecovered_DropsEntriesSonarNoLongerShows()
    {
        var list = new HuntList();
        list.ReplaceMemory([Mem("Rusalka", 100, 3, T0), Mem("Baal", 100, 2, T0)], T0, announce: false);
        list.MemoryLost();
        list.ReplaceMemory([Mem("Baal", 100, 2, T0)], T0.AddMinutes(2), announce: true);
        Assert.Equal(["Baal"], list.Visible(HuntFilter.All).Select(x => x.Name));
    }

    [Fact]
    public void DeadHuntInSonarMemory_FollowsSonarNotOwnTimer()
    {
        var list = new HuntList();
        list.ReplaceMemory([Mem("Rusalka", 0, 3, T0, dead: true)], T0, announce: false);
        list.ReplaceMemory([Mem("Rusalka", 0, 3, T0, dead: true)], T0.AddMinutes(6), announce: true);
        list.Expire(T0.AddMinutes(6));
        Assert.Single(list.Visible(HuntFilter.All));
    }

    [Fact]
    public void IsNews_OnlyRecentlyFound()
    {
        var list = new HuntList();
        var fresh = list.ReplaceMemory([Mem("Rusalka", 100, 3, T0.AddSeconds(-30))], T0, announce: true);
        var old = list.ReplaceMemory([Mem("Rusalka", 100, 3, T0.AddSeconds(-30)), Mem("Baal", 100, 2, T0.AddMinutes(-10))], T0, announce: true);
        Assert.True(HuntList.IsNews(Assert.Single(fresh), T0));
        Assert.False(HuntList.IsNews(Assert.Single(old), T0));
    }

    [Fact]
    public void ReplaceMemory_WithoutAnnounce_ReturnsNoNewEntries()
    {
        var list = new HuntList();
        var created = list.ReplaceMemory([Mem("Rusalka", 100, 1, T0), Mem("Baal", 100, 1, T0), Mem("Supay", 100, 1, T0)], T0, announce: false);
        Assert.Empty(created);
        Assert.Equal(3, list.Visible(HuntFilter.All).Count);

        var next = list.ReplaceMemory([Mem("Rusalka", 100, 1, T0), Mem("Baal", 100, 1, T0), Mem("Supay", 100, 1, T0), Mem("Sugriva", 100, 1, T0)], T0.AddSeconds(1), announce: true);
        Assert.Equal(["Sugriva"], next.Select(e => e.Name));
    }

    [Fact]
    public void MemoryLost_EntriesFallBackToOwnTimers()
    {
        var list = new HuntList();
        list.ReplaceMemory([Mem("Rusalka", 100, 1, T0)], T0, announce: false);
        list.Expire(T0.AddMinutes(61));
        Assert.Single(list.Visible(HuntFilter.All));

        list.MemoryLost();
        list.Expire(T0.AddMinutes(61));
        Assert.Empty(list.Visible(HuntFilter.All));
    }

    [Fact]
    public void StateOf_UntouchedPulledFate()
    {
        var list = new HuntList();
        list.Upsert(Chat("Rusalka"), T0);
        list.Upsert(Chat("Baal", pct: 62), T0);
        list.Upsert(Chat("Coeurl", HuntKind.Fate, pct: 45), T0);
        var byName = list.Visible(HuntFilter.All).ToDictionary(e => e.Name);
        Assert.Equal(HuntRowState.Untouched, HuntList.StateOf(byName["Rusalka"]));
        Assert.Equal(HuntRowState.Pulled, HuntList.StateOf(byName["Baal"]));
        Assert.Equal(HuntRowState.Fate, HuntList.StateOf(byName["Coeurl"]));
    }

    [Fact]
    public void SetLivePercent_UpdatesMatchingEntry()
    {
        var list = new HuntList();
        var e = list.Upsert(Chat("Rusalka"), T0)!;
        list.SetLivePercent(e.Key, 40, T0.AddSeconds(3));
        Assert.Equal(40, e.Percent);
        Assert.Equal(HuntRowState.Pulled, HuntList.StateOf(e));
    }

    [Fact]
    public void Sound_FollowsTheSwitchOfEachKind()
    {
        Assert.True(HuntSounds.Wants(HuntKind.S, s: true, a: false, fate: false));
        Assert.False(HuntSounds.Wants(HuntKind.A, s: true, a: false, fate: false));
        Assert.True(HuntSounds.Wants(HuntKind.A, s: false, a: true, fate: false));
        Assert.False(HuntSounds.Wants(HuntKind.Fate, s: true, a: true, fate: false));
        Assert.True(HuntSounds.Wants(HuntKind.Fate, s: false, a: false, fate: true));
    }
}
