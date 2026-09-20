using System.Diagnostics;
using System.Text;

namespace MicrosoftPurviewScreenGuard;

internal enum LogKind
{
    Info,
    Word,
    Camera,
    Phone,
    Screen,
    Warn,
    Error,
}

/// <summary>A piece of a log line with its own color (null = the terminal's default).</summary>
internal readonly record struct Seg(string Text, ConsoleColor? Color = null);

/// <summary>
/// Console log in columns: time, category tag, message. Each category has its own color, phone hits show a score bar.
/// Colors are only used on a real console; when the output is redirected to a file the same text is written without any
/// escape codes.
/// </summary>
internal static class Logger
{
    private static readonly object Gate = new();
    private static readonly bool UseColor = !Console.IsOutputRedirected;
    private static readonly bool UseUnicode = TryUtf8();

    /// <summary>Time since the first log line (start of the app); used to see where startup time goes.</summary>
    public static readonly Stopwatch Uptime = Stopwatch.StartNew();

    private const int BarCells = 20;

    private static bool TryUtf8()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- simple lines

    public static void Log(string message) => Write(LogKind.Info, message);

    public static void Info(string message) => Write(LogKind.Info, message);

    public static void Warn(string message) => Write(LogKind.Warn, message);

    public static void Error(string message) => Write(LogKind.Error, message);

    public static void Write(LogKind kind, string message, DateTime? at = null) =>
        Write(kind, at, new Seg(message, MessageColor(kind)));

    /// <summary>A line made of differently colored pieces.</summary>
    public static void Write(LogKind kind, params Seg[] parts) => Write(kind, null, parts);

    public static void Write(LogKind kind, DateTime? at, params Seg[] parts) =>
        Emit(at ?? DateTime.Now, Tag(kind), TagColor(kind), parts);

    // ---------------------------------------------------------------- specific lines

    /// <summary>Screen state change: a colored headline (BLOCKED / clear) followed by the reasons.</summary>
    public static void Screen(bool blocked, string headline, string details) =>
        Write(
            LogKind.Screen,
            new Seg(headline.PadRight(9), blocked ? ConsoleColor.Red : ConsoleColor.Green),
            new Seg(details, ConsoleColor.Gray));

    /// <summary>One analyzed camera frame with a phone score, drawn as a bar. Timestamped with the moment the frame was captured.</summary>
    public static void Hit(DateTime capturedAt, double score, string verdict, ConsoleColor color) =>
        Emit(
            capturedAt,
            "HIT",
            ConsoleColor.Magenta,
            new Seg(score.ToString("0.00") + "  ", color),
            new Seg(Bar(score), color),
            new Seg("  " + verdict, color));

    /// <summary>The once-per-second camera status. Dim while everything is fine, yellow when the camera is not healthy.</summary>
    public static void CameraStatus(bool healthy, string text) =>
        Write(LogKind.Camera, new Seg(text, healthy ? ConsoleColor.DarkGray : ConsoleColor.Yellow));

    /// <summary>A block of lines without time and tag (the start-up header).</summary>
    public static void Banner(params string[] lines)
    {
        lock (Gate)
        {
            Console.WriteLine();
            for (int i = 0; i < lines.Length; i++)
            {
                Put("  " + lines[i], i == 0 ? ConsoleColor.White : ConsoleColor.Gray);
                Console.WriteLine();
            }

            Put("  " + new string(UseUnicode ? '─' : '-', 62), ConsoleColor.DarkGray);
            Console.WriteLine();
            Console.WriteLine();
        }
    }

    /// <summary>Filled bar for a 0..1 score: the more filled, the more it looks like a phone.</summary>
    public static string Bar(double score)
    {
        int filled = (int)Math.Round(Math.Clamp(score, 0, 1) * BarCells);
        char on = UseUnicode ? '█' : '#';
        char off = UseUnicode ? '░' : '-';
        return new string(on, filled) + new string(off, BarCells - filled);
    }

    // ---------------------------------------------------------------- output

    private static void Emit(DateTime time, string tag, ConsoleColor tagColor, params Seg[] parts)
    {
        lock (Gate)
        {
            Put(time.ToString("HH:mm:ss.fff"), ConsoleColor.DarkGray);
            Put("  ", null);
            Put(tag.PadRight(7), tagColor);
            foreach (Seg part in parts)
            {
                Put(part.Text, part.Color);
            }

            Console.WriteLine();
        }
    }

    private static void Put(string text, ConsoleColor? color)
    {
        if (UseColor && color is { } c)
        {
            Console.ForegroundColor = c;
            Console.Write(text);
            Console.ResetColor();
        }
        else
        {
            Console.Write(text);
        }
    }

    private static string Tag(LogKind kind) => kind switch
    {
        LogKind.Word => "WORD",
        LogKind.Camera => "CAMERA",
        LogKind.Phone => "PHONE",
        LogKind.Screen => "SCREEN",
        LogKind.Warn => "WARN",
        LogKind.Error => "ERROR",
        _ => "INFO",
    };

    private static ConsoleColor TagColor(LogKind kind) => kind switch
    {
        LogKind.Word => ConsoleColor.Cyan,
        LogKind.Camera => ConsoleColor.DarkCyan,
        LogKind.Phone => ConsoleColor.Magenta,
        LogKind.Screen => ConsoleColor.White,
        LogKind.Warn => ConsoleColor.Yellow,
        LogKind.Error => ConsoleColor.Red,
        _ => ConsoleColor.DarkGray,
    };

    private static ConsoleColor? MessageColor(LogKind kind) => kind switch
    {
        LogKind.Warn => ConsoleColor.Yellow,
        LogKind.Error => ConsoleColor.Red,
        LogKind.Info => ConsoleColor.Gray,
        _ => null,
    };
}
