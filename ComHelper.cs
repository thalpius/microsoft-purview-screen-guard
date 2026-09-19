using System.Runtime.InteropServices;

namespace MicrosoftPurviewScreenGuard;

internal static class ComHelper
{
    // MK_E_UNAVAILABLE: the object is not registered in the running object table.
    private const int MK_E_UNAVAILABLE = unchecked((int)0x800401E3);

    /// <summary>
    /// Returns the running COM object for a ProgID, or null if none is running.
    /// Replacement for Marshal.GetActiveObject, which does not exist in .NET 8.
    /// </summary>
    public static object? GetActiveObject(string progId)
    {
        int hr = Native.CLSIDFromProgID(progId, out Guid clsid);
        if (hr < 0)
        {
            return null;
        }

        hr = Native.GetActiveObject(ref clsid, IntPtr.Zero, out object? obj);
        if (hr == MK_E_UNAVAILABLE)
        {
            return null;
        }

        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return obj;
    }

    /// <summary>Releases a COM object if it is one; never throws.</summary>
    public static void Release(object? obj)
    {
        try
        {
            if (obj is not null && Marshal.IsComObject(obj))
            {
                Marshal.FinalReleaseComObject(obj);
            }
        }
        catch
        {
            // Releasing is best effort.
        }
    }
}
