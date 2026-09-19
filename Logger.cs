namespace MicrosoftPurviewScreenGuard;

internal static class Logger
{
    private static readonly object Gate = new();

    /// <summary>Time since the first log line (start of the app); used to see where startup time goes.</summary>
    public static readonly System.Diagnostics.Stopwatch Uptime = System.Diagnostics.Stopwatch.StartNew();

    public static void Log(string message)
    {
        lock (Gate)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }
}
