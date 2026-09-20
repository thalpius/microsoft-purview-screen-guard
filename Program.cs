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

        Logger.Banner(
            "Microsoft Purview Screen Guard   -   proof of concept",
            "Covers all screens when a sensitive Word document is visible AND (a phone is seen OR the camera is not healthy).",
            "Keys: P = toggle the manual \"phone seen\" override (debug)    Ctrl+C = stop");

        var blocked = BlockedLabels.Load(AppContext.BaseDirectory, Logger.Warn);
        Logger.Info($"{blocked.Count} blocked label GUID(s) loaded");

        using var context = new GuardContext(blocked);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            context.RequestExit();
        };

        Application.Run(context);

        Logger.Info("stopped");
        return 0;
    }
}
