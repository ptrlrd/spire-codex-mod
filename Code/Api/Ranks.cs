namespace SpireCodex.Api;

public static class Ranks
{
    // Codex Score (0-100) -> the same tier bands the website/API publish.
    public static string Tier(double score) =>
        score >= 90 ? "S" : score >= 78 ? "A" : score >= 65 ? "B"
        : score >= 50 ? "C" : score >= 35 ? "D" : "F";
}
