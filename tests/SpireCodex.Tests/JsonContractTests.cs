using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace SpireCodex.Tests;

// Two DTO properties carrying the same [JsonPropertyName] compile fine and then throw
// InvalidOperationException the first time anything deserializes that type. In the mod that
// exception lands inside CodexScores' catch, so the only symptom is that no scores ever
// load: no plates, no Elo, no bracket switching, and nothing on screen saying why.
//
// It happened for real when the SKIP fields were added and `picks` was mapped twice. These
// tests read the client source directly, because the DTOs are private nested classes in an
// assembly that needs Godot to load, and a guard that only works with the game running is
// no guard at all.
public sealed class JsonContractTests
{
    private static string ClientSource()
    {
        var dir = Path.GetDirectoryName(ThisFile())!;
        return File.ReadAllText(Path.Combine(dir, "..", "..", "Code", "Api", "SpireCodexClient.cs"));
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private readonly record struct ClassBlock(string Name, int Start, int End);

    // Every class block with its balanced body span. Nested classes appear too, so a
    // property is attributed to the INNERMOST block containing it — the DTOs are nested
    // inside SpireCodexClient, and naively scanning the outer body would credit it with
    // every DTO's fields and report collisions that don't exist.
    private static List<ClassBlock> ClassBlocks(string src)
    {
        var blocks = new List<ClassBlock>();
        foreach (Match cls in Regex.Matches(src, @"class\s+(\w+)"))
        {
            var open = src.IndexOf('{', cls.Index);
            if (open < 0) continue;
            var depth = 0;
            var i = open;
            for (; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) break;
            }
            blocks.Add(new ClassBlock(cls.Groups[1].Value, open, i));
        }
        return blocks;
    }

    private static IEnumerable<(string Name, List<string> Props)> DtoClasses(string src)
    {
        var blocks = ClassBlocks(src);
        var byClass = new Dictionary<string, List<string>>();
        foreach (Match m in Regex.Matches(src, @"JsonPropertyName\(""([^""]+)""\)"))
        {
            // Smallest span containing this attribute = the class that declares it.
            var owner = blocks
                .Where(b => m.Index > b.Start && m.Index < b.End)
                .OrderBy(b => b.End - b.Start)
                .FirstOrDefault();
            if (owner.Name is null) continue;
            if (!byClass.TryGetValue(owner.Name, out var list))
                byClass[owner.Name] = list = new List<string>();
            list.Add(m.Groups[1].Value);
        }
        foreach (var (name, props) in byClass) yield return (name, props);
    }

    [Fact]
    public void NoDtoMapsTheSameJsonNameTwice()
    {
        foreach (var (name, props) in DtoClasses(ClientSource()))
        {
            var dupes = props.GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(
                dupes.Count == 0,
                $"{name} maps {string.Join(", ", dupes)} more than once. "
                + "System.Text.Json throws on the first deserialize, and the mod swallows it "
                + "into a silent no-scores state.");
        }
    }

    // The fields the mod reads off the reserved SKIP entry. `picks` and `offered` count
    // reward screens across the whole playerbase (36.5M and climbing), so they must not be
    // int, and `score`/`win_rate` arrive null for SKIP so they must be nullable.
    [Theory]
    [InlineData("picks", "long")]
    [InlineData("offered", "long")]
    [InlineData("picked", "long?")]
    [InlineData("score", "double?")]
    [InlineData("win_rate", "double?")]
    public void SkipSafeFieldsKeepTheirWidthAndNullability(string json, string type)
    {
        var src = ClientSource();
        var m = Regex.Match(src, $@"JsonPropertyName\(""{Regex.Escape(json)}""\)\]\s*public\s+(\S+)\s");
        Assert.True(m.Success, $"no property mapped to \"{json}\" in SpireCodexClient");
        Assert.Equal(type, m.Groups[1].Value);
    }
}
