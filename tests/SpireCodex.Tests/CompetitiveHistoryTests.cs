using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

public sealed class CompetitiveHistoryTests
{
    private static ReplayJournalScan.CompetitiveHistory Scan(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"comp-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        var history = ReplayJournalScan.CompetitiveOf(path);
        File.Delete(path);
        return history;
    }

    private const string HeaderOn = """{"t":"header","s":0,"competitive":true}""";
    private const string HeaderOff = """{"t":"header","s":0,"competitive":false}""";
    private const string Fight = """{"t":"combat_start","s":1,"combat_id":"3:CULTISTS"}""";
    private const string Won = """{"t":"combat_end","s":2,"combat_id":"3:CULTISTS"}""";

    [Fact]
    public void ACrashMidFightStaysClean()
    {
        var h = Scan(HeaderOn, Fight, """{"t":"end","s":2,"terminal_reason":"interrupted"}""", HeaderOn);
        Assert.True(h.StartedOn);
        Assert.Null(h.Flag);
        Assert.Equal(1, h.CrashRecoveries);
    }

    [Fact]
    public void LeavingMidFightIsAReload()
    {
        var h = Scan(HeaderOn, Fight, """{"t":"end","s":2,"terminal_reason":"left_run"}""", HeaderOn);
        Assert.Equal("reload", h.Flag);
        Assert.Equal(0, h.CrashRecoveries);
    }

    [Fact]
    public void ClosingTheGameMidFightIsAReload()
    {
        var h = Scan(HeaderOn, Fight, """{"t":"competitive","s":2,"event":"quit"}""",
            """{"t":"end","s":3,"terminal_reason":"interrupted"}""", HeaderOn);
        Assert.Equal("reload", h.Flag);
        Assert.Equal(0, h.CrashRecoveries);
    }

    [Fact]
    public void ACleanExitMidFightIsAReload()
    {
        var h = Scan(HeaderOn, Fight, """{"t":"end","s":2,"terminal_reason":"interrupted","exit":"clean"}""", HeaderOn);
        Assert.Equal("reload", h.Flag);
    }

    [Fact]
    public void OneUnexplainedKillIsToleratedAndTheSecondIsNot()
    {
        const string killed = """{"t":"end","s":2,"terminal_reason":"interrupted","exit":"killed"}""";
        var once = Scan(HeaderOn, Fight, killed, HeaderOn);
        Assert.Null(once.Flag);
        Assert.Equal(1, once.UnexplainedExits);

        var twice = Scan(HeaderOn, Fight, killed, HeaderOn, Fight, killed, HeaderOn);
        Assert.Equal("killed", twice.Flag);
    }

    [Fact]
    public void ARecordedCrashNeverCountsAsUnexplained()
    {
        const string crash = """{"t":"end","s":2,"terminal_reason":"interrupted","exit":"crash"}""";
        var h = Scan(HeaderOn, Fight, crash, HeaderOn, Fight, crash, HeaderOn);
        Assert.Null(h.Flag);
        Assert.Equal(2, h.CrashRecoveries);
        Assert.Equal(0, h.UnexplainedExits);
    }

    [Fact]
    public void LogTailsAreClassified()
    {
        Assert.Equal("clean", ReplayJournalScan.ExitKind("ERROR: 61 resources still in use at exit (run with --verbose for details)."));
        Assert.Equal("crash", ReplayJournalScan.ExitKind("CrashHandlerException: Program crashed\nDumping the backtrace."));
        Assert.Equal("killed", ReplayJournalScan.ExitKind("[INFO] [SpireCodex] replay: recording 1.jsonl"));
    }

    [Fact]
    public void LeavingBetweenFightsIsAQuit()
    {
        var h = Scan(HeaderOn, Fight, Won, """{"t":"end","s":3,"terminal_reason":"left_run"}""", HeaderOn);
        Assert.Equal("quit", h.Flag);
    }

    [Fact]
    public void ARunStartedWithoutTheModeIsNotStartedOn()
    {
        var h = Scan(HeaderOff, HeaderOn);
        Assert.False(h.StartedOn);
        Assert.Null(h.Flag);
    }

    [Fact]
    public void TurningTheModeOffLaterIsRemembered()
    {
        var h = Scan(HeaderOn, Fight, Won, """{"t":"end","s":3,"terminal_reason":"interrupted","exit":"crash"}""", HeaderOff, HeaderOn);
        Assert.Equal("toggled", h.Flag);
    }

    [Fact]
    public void AFlagRowSticks()
    {
        var h = Scan(HeaderOn, """{"t":"competitive","s":1,"flag":"late"}""");
        Assert.Equal("late", h.Flag);
    }

    private static ReplayJournalScan.Progress Seen(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"comp-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        var seen = ReplayJournalScan.ProgressOf(path);
        File.Delete(path);
        return seen;
    }

    private const string Floor5 = """{"t":"combat_start","s":4,"ms":10,"floor":5,"act":1,"combat_id":"1.5:CULTISTS"}""";
    private const string Floor5Won = """{"t":"combat_end","s":5,"ms":20,"floor":5,"act":1,"combat_id":"1.5:CULTISTS"}""";

    [Fact]
    public void ResumingOnAnEarlierFloorIsARollback()
    {
        var seen = Seen("""{"t":"header","s":0,"floor":1,"reloads":0}""", Floor5);
        Assert.Equal(5, seen.MaxFloor);
        Assert.True(ReplayJournalScan.RolledBack(seen, 4, 1));
        Assert.False(ReplayJournalScan.RolledBack(seen, 5, 1));
    }

    [Fact]
    public void AnOlderReloadCountIsARollback()
    {
        var seen = Seen("""{"t":"header","s":0,"floor":1,"reloads":0}""", """{"t":"resume","s":9,"floor":3,"reloads":2}""");
        Assert.True(ReplayJournalScan.RolledBack(seen, 3, 1));
        Assert.False(ReplayJournalScan.RolledBack(seen, 3, 3));
    }

    [Fact]
    public void FinishedFightsAreRemembered()
    {
        var seen = Seen(Floor5, Floor5Won);
        Assert.Contains("1.5:CULTISTS", seen.EndedCombats);
    }

    [Fact]
    public void NestedFloorsAreIgnored()
    {
        var seen = Seen("""{"t":"map","s":1,"ms":5,"nodes":[{"floor":40}]}""", Floor5);
        Assert.Equal(5, seen.MaxFloor);
    }

    [Fact]
    public void OnlyTheTwoBracketsCount()
    {
        Assert.Equal("a10_solo", ReplayJournalScan.Bracket("Standard", 10, 1, 0));
        Assert.Equal("dread_solo", ReplayJournalScan.Bracket("Dread", 0, 1, 0));
        Assert.Null(ReplayJournalScan.Bracket("Standard", 9, 1, 0));
        Assert.Null(ReplayJournalScan.Bracket("Standard", 10, 2, 0));
        Assert.Null(ReplayJournalScan.Bracket("Standard", 10, 1, 1));
        Assert.Null(ReplayJournalScan.Bracket("Daily", 10, 1, 0));
    }
}
