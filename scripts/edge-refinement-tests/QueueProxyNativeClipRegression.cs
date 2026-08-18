using System.IO;

internal static class QueueProxyNativeClipRegression
{
    public static void Run()
    {
        var visuals = Source(
            "EdgeCapsuleQueueCompositionProxy.Visuals.cs");
        foreach (var forbidden in new[]
        {
            "CreateRectangleClip",
            "CreateEffectGroup",
            "SetClip(",
            "SetOpacity(",
            "ClipLeftAnimation",
            "OpacityAnimation"
        })
        {
            Require(
                !visuals.Contains(forbidden),
                $"translation backend regrew {forbidden}");
        }
        Require(
            visuals.Contains("SetOffsetX") &&
            visuals.Contains("SetOffsetY"),
            "translation backend must own offsets");
    }

    private static string Source(string name)
    {
        var directory =
            new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(
                   directory.FullName,
                   "PaperTodo.csproj")))
        {
            directory = directory.Parent;
        }
        if (directory == null)
        {
            throw new InvalidOperationException(
                "repository root not found");
        }
        return File.ReadAllText(
            Path.Combine(directory.FullName, name));
    }

    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
