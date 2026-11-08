using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static unsafe partial class Native
{
    internal const uint WmCopyData = 0x004A;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyData
    {
        internal nuint DataId;
        internal uint ByteCount;
        internal void* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal char* MenuName;
        internal char* ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Hwnd;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    internal static nint Send(nint hwnd, nuint id, ReadOnlySpan<byte> bytes, nint sender = 0)
    {
        fixed (byte* data = bytes)
        {
            CopyData message = new() { DataId = id, ByteCount = (uint)bytes.Length, Data = data };
            return SendMessage(hwnd, WmCopyData, unchecked((nuint)sender), (nint)(&message));
        }
    }

    internal static nint SendTimed(nint hwnd, nuint id, byte[] bytes, nint sender)
    {
        fixed (byte* data = bytes)
        {
            CopyData message = new() { DataId = id, ByteCount = (uint)bytes.Length, Data = data };
            if (SendMessageTimeout(hwnd, WmCopyData, unchecked((nuint)sender), (nint)(&message),
                0x22, 5000, out nuint result) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return unchecked((nint)result);
        }
    }

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? name);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(in WindowClass windowClass);
    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int UnregisterClass(string name, nint instance);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")]
    internal static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial int IsWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial int IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static partial nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    internal static partial int PeekMessage(out Message message, nint hwnd, uint minimum, uint maximum, uint remove);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in Message message);
}

internal sealed unsafe class TestWindow : IDisposable
{
    internal const int DefaultReply = 317;
    internal const uint OtherMessage = 0x8001;
    private static readonly Dictionary<nint, TestWindow> Windows = new();
    private readonly string _name = "ReceiveCopyData.Smoke." + Guid.NewGuid().ToString("N");
    private readonly nint _instance = Native.GetModuleHandle(null);
    internal nint Hwnd { get; private set; }
    internal int CopyDataCalls { get; private set; }

    internal TestWindow()
    {
        fixed (char* name = _name)
        {
            Native.WindowClass definition = new()
            {
                Size = (uint)sizeof(Native.WindowClass), Procedure = &WindowProcedure,
                Instance = _instance, ClassName = name
            };
            if (Native.RegisterClassEx(in definition) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        Hwnd = Native.CreateWindowEx(0x08000080, _name, "", 0x80000000,
            0, 0, 1, 1, 0, 0, _instance, 0); // No WS_VISIBLE and no activation.
        if (Hwnd == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            _ = Native.UnregisterClass(_name, _instance);
            throw new Win32Exception(error);
        }
        Windows.Add(Hwnd, this);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Windows.TryGetValue(hwnd, out TestWindow? window))
        {
            if (message == Native.WmCopyData)
            {
                window.CopyDataCalls++;
                return DefaultReply;
            }
            if (message == OtherMessage)
                return 719;
        }
        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    internal void Destroy()
    {
        if (Hwnd == 0)
            return;
        nint hwnd = Hwnd;
        if (Native.DestroyWindow(hwnd) == 0)
            throw new InvalidOperationException("DestroyWindow failed.");
        Windows.Remove(hwnd);
        Hwnd = 0;
    }

    public void Dispose()
    {
        Destroy();
        if (Native.UnregisterClass(_name, _instance) == 0)
            throw new InvalidOperationException("UnregisterClass failed.");
    }
}
