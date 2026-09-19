namespace MicrosoftPurviewScreenGuard;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Must run before any window is created.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The UI thread paints the overlay; keep it ahead of the detector (BelowNormal) and the camera reader (Normal).
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

        Logger.Log("Microsoft Purview Screen Guard - milestone 5 (overlay picture). Ctrl+C to stop.");
        Logger.Log("Thread priorities: UI AboveNormal, camera reader Normal, phone detector BelowNormal");

        var blocked = BlockedLabels.Load(AppContext.BaseDirectory, msg => Logger.Log("WARNING: " + msg));
        Logger.Log($"Blocked label GUIDs loaded: {blocked.Count}");

        using var context = new GuardContext(blocked);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            context.RequestExit();
        };

        Application.Run(context);

        Logger.Log("Stopped.");
        return 0;
    }
}
