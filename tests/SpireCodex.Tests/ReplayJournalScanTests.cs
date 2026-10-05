using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

public sealed class ReplayJournalScanTests
{
    private static string WriteJournal(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scan-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void RecoversTheHighestIdOfEachKind()
    {
        var path = WriteJournal(
            """{"t":"header","s":0,"starting_deck":[{"c":1,"id":"STRIKE"},{"c":2,"id":"DEFEND"}]}""",
            """{"t":"draw","s":1,"c":9,"deck_c":2}""",
            """{"t":"decision","s":2,"decision_id":4}""",
            """{"t":"transform","s":3,"from_c":9,"to_c":31}""",
            """{"t":"pick","s":4,"instance_id":27,"decision_id":3}""");

        var hw = ReplayJournalScan.HighWaterOf(path);
        File.Delete(path);

        Assert.Equal(4, hw.Seq);
        Assert.Equal(31, hw.Card);
        Assert.Equal(4, hw.Decision);
    }

    [Fact]
    public void RecoversCardIdsThatOnlyAppearInsideArrays()
    {
        var path = WriteJournal(
            """{"t":"draw","s":0,"c":3,"deck_c":1}""",
            """{"t":"draw_order","s":1,"order_c":[12,40,7],"order_deck_c":[2,1,3]}""",
            """{"t":"flush","s":2,"flushed_c":[],"retained_c":[9]}""",
            """{"t":"shuffle","s":3,"n_draw":2,"order_c":[5,6]}""");

        var hw = ReplayJournalScan.HighWaterOf(path);
        File.Delete(path);

        Assert.Equal(40, hw.Card);
    }

    [Fact]
    public void ResumesTheCreatureIdFromEveryKeyThatCarriesOne()
    {
        var path = WriteJournal(
            """{"t":"combat_start","s":0,"enemies":[{"i":0,"cid":3,"id":"CORPSE_SLUG"}]}""",
            """{"t":"play","s":1,"target_cid":4}""",
            """{"t":"hit","s":2,"src_cid":5,"dst_cid":9}""",
            """{"t":"power","s":3,"src_cid":6,"tgt_cid":7}""",
            """{"t":"move","s":4,"src_cid":2}""");

        var hw = ReplayJournalScan.HighWaterOf(path);
        File.Delete(path);

        Assert.Equal(9, hw.Creature);
    }

    [Fact]
    public void DoesNotReadACreatureIdOffAKeyThatMerelyEndsInCid()
    {
        var path = WriteJournal(
            """{"t":"play","s":1,"target_cid":4,"not_a_cid":4000}""");

        var hw = ReplayJournalScan.HighWaterOf(path);
        File.Delete(path);

        Assert.Equal(4, hw.Creature);
    }

    [Fact]
    public void DoesNotConfuseAKeyWithOneThatEndsInIt()
    {
        var path = WriteJournal(
            """{"t":"play","s":7,"ms":999999,"c":3,"deck_c":800,"stars_paid":500,"cost_paid":2}""");

        var hw = ReplayJournalScan.HighWaterOf(path);
        File.Delete(path);

        Assert.Equal(7, hw.Seq);
        Assert.Equal(800, hw.Card);
    }

    [Fact]
    public void ReadsTheWholeFileBecauseTheMaximaAreNotAtTheTail()
    {
        var lines = new List<string> { """{"t":"acquire","s":0,"c":742,"decision_id":88}""" };
        for (var i = 1; i < 4000; i++)
            lines.Add($$"""{"t":"play","s":{{i}},"c":4,"deck_c":2}""");

        var path = WriteJournal(lines.ToArray());
        var hw = ReplayJournalScan.HighWaterOf(path);
        File.Delete(path);

        Assert.Equal(742, hw.Card);
        Assert.Equal(88, hw.Decision);
    }

    [Fact]
    public void ANewFileYieldsNoMarksSoAFreshRunStartsAtOne()
    {
        var hw = ReplayJournalScan.HighWaterOf(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.jsonl"));

        Assert.Equal(-1, hw.Seq);
        Assert.Equal(0, hw.Card);
        Assert.Equal(0, hw.Decision);
    }

    [Fact]
    public void SequenceStillMatchesTheTailReadItReplaced()
    {
        var path = WriteJournal(
            """{"t":"header","s":0}""",
            """{"t":"draw","s":1,"c":5}""",
            """{"t":"end","s":2}""");

        var hw = ReplayJournalScan.HighWaterOf(path);
        var tail = ReplayJournalScan.LastSequence(path);
        File.Delete(path);

        Assert.Equal(tail, hw.Seq);
    }

    [Fact]
    public void AFightTheProcessDiedInIsTheOpenCombat()
    {
        var path = WriteJournal(
            """{"t":"combat_start","s":0,"combat_id":"1.5:CULTIST"}""",
            """{"t":"combat_end","s":1,"result":"victory","combat_id":"1.5:CULTIST"}""",
            """{"t":"combat_start","s":2,"combat_id":"1.6:SLUDGE_SPINNER_WEAK"}""",
            """{"t":"play","s":3,"id":"ANGER"}""");

        var open = ReplayJournalScan.OpenCombat(path);
        File.Delete(path);

        Assert.Equal("1.6:SLUDGE_SPINNER_WEAK", open);
    }

    [Fact]
    public void AFinishedFightLeavesNoCombatOpen()
    {
        var path = WriteJournal(
            """{"t":"combat_start","s":0,"combat_id":"1.6:SLUDGE_SPINNER_WEAK"}""",
            """{"t":"combat_end","s":1,"result":"victory","combat_id":"1.6:SLUDGE_SPINNER_WEAK"}""",
            """{"t":"decision","s":2,"decision_id":4}""");

        var open = ReplayJournalScan.OpenCombat(path);
        File.Delete(path);

        Assert.Null(open);
    }

    [Fact]
    public void AFightResumedAfterAReloadAndFinishedIsClosed()
    {
        var path = WriteJournal(
            """{"t":"combat_start","s":0,"combat_id":"1.6:SLUDGE_SPINNER_WEAK"}""",
            """{"t":"end","s":1,"terminal_reason":"interrupted","capture_status":"truncated"}""",
            """{"t":"resume","s":2,"combat_id":"1.6:SLUDGE_SPINNER_WEAK"}""",
            """{"t":"combat_end","s":3,"result":"victory","combat_id":"1.6:SLUDGE_SPINNER_WEAK"}""");

        var open = ReplayJournalScan.OpenCombat(path);
        File.Delete(path);

        Assert.Null(open);
    }
}
