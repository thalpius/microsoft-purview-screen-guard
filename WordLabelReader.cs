using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace MicrosoftPurviewScreenGuard;

internal enum WordStatus
{
    NotRunning,

    /// <summary>A COM call failed (Word busy, dialog open, shutting down). Keep the previous state.</summary>
    Busy,
    Ok,
}

/// <summary>One open Word window and the sensitivity label of the document it shows.</summary>
/// <param name="LabelId">Label GUID, or null when the document has no label.</param>
/// <param name="LabelUnavailable">True when the SensitivityLabel API could not be used for this document.</param>
internal sealed record WordWindowState(
    long Hwnd,
    string DocumentName,
    string? LabelId,
    bool LabelUnavailable,
    bool Blocked,
    bool Visible,
    bool Minimized);

internal sealed record WordSnapshot(
    WordStatus Status,
    IReadOnlyList<WordWindowState> Windows,
    string? Error = null);

internal sealed class WordLabelReader
{
    private readonly BlockedLabels _blocked;

    public WordLabelReader(BlockedLabels blocked) => _blocked = blocked;

    /// <summary>Lists every open Word window with its label. Never throws on COM errors.</summary>
    public WordSnapshot Read()
    {
        object? app = null;
        try
        {
            app = ComHelper.GetActiveObject("Word.Application");
            if (app is null)
            {
                return new WordSnapshot(WordStatus.NotRunning, Array.Empty<WordWindowState>());
            }

            return new WordSnapshot(WordStatus.Ok, ReadWindows(app));
        }
        catch (COMException ex)
        {
            return new WordSnapshot(WordStatus.Busy, Array.Empty<WordWindowState>(), $"0x{ex.HResult:X8} {ex.Message.Trim()}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail closed: any unreadable round must keep the previous state, never read as "not sensitive".
            return new WordSnapshot(WordStatus.Busy, Array.Empty<WordWindowState>(), $"{ex.GetType().Name}: {ex.Message.Trim()}");
        }
        finally
        {
            ComHelper.Release(app);
        }
    }

    private List<WordWindowState> ReadWindows(object app)
    {
        var result = new List<WordWindowState>();

        dynamic word = app;
        dynamic windows = word.Windows;
        try
        {
            int count = windows.Count;
            for (int i = 1; i <= count; i++)
            {
                object window = windows.Item(i);
                try
                {
                    result.Add(ReadWindow(window));
                }
                finally
                {
                    ComHelper.Release(window);
                }
            }
        }
        finally
        {
            ComHelper.Release(windows);
        }

        result.Sort((a, b) => a.Hwnd.CompareTo(b.Hwnd));
        return result;
    }

    private WordWindowState ReadWindow(object windowObject)
    {
        dynamic window = windowObject;
        long hwnd = window.Hwnd;
        var handle = new IntPtr(hwnd);

        object document = window.Document;
        try
        {
            dynamic doc = document;
            string name = doc.Name;
            (string? labelId, bool unavailable) = ReadLabelId(document);
            return new WordWindowState(
                hwnd,
                name,
                labelId,
                unavailable,
                Blocked: _blocked.Contains(labelId),
                Visible: Native.IsWindowVisible(handle),
                Minimized: Native.IsIconic(handle));
        }
        finally
        {
            ComHelper.Release(document);
        }
    }

    /// <summary>doc.SensitivityLabel.GetLabel().LabelId. Matched on GUID only; LabelName can be empty.</summary>
    private static (string? LabelId, bool Unavailable) ReadLabelId(object documentObject)
    {
        dynamic document = documentObject;
        object? sensitivityLabel = null;
        object? info = null;
        try
        {
            sensitivityLabel = document.SensitivityLabel;
            info = ((dynamic)sensitivityLabel).GetLabel();
            string? id = info is null ? null : ((dynamic)info).LabelId;
            return (string.IsNullOrWhiteSpace(id) ? null : id.Trim(), false);
        }
        catch (RuntimeBinderException)
        {
            // Word version without the SensitivityLabel API.
            return (null, true);
        }
        finally
        {
            ComHelper.Release(info);
            ComHelper.Release(sensitivityLabel);
        }
    }
}
