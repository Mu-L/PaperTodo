namespace PaperTodo;

// EdgeCapsuleHoverIntent.cs is linked into this small policy test assembly without the full app
// model. Keep the production sensitivity spellings here so the pure predictor can be exercised.
internal static class EdgeCapsuleHoverIntentSensitivities
{
    public const string VeryLow = "veryLow";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string VeryHigh = "veryHigh";

    public static string Normalize(string? value) => value switch
    {
        VeryLow => VeryLow,
        Low => Low,
        High => High,
        VeryHigh => VeryHigh,
        _ => Medium
    };
}
