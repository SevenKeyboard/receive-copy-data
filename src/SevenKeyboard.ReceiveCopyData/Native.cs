using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace SevenKeyboard;

internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyData
    {
        internal nuint DataId;
        internal uint ByteCount;
        internal void* Data;
    }

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("comctl32.dll")]
    internal static partial int SetWindowSubclass(nint hwnd,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback,
        nuint id, nuint referenceData);

    [LibraryImport("comctl32.dll")]
    internal static partial int RemoveWindowSubclass(nint hwnd,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id);

    [LibraryImport("comctl32.dll")]
    internal static partial nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
