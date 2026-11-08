//==============================================================
// ReceiveCopyData — WM_COPYDATA dispatcher with dwData-to-callback binding
//
// GitHub: https://github.com/SevenKeyboard/receive-copy-data
// Author: SevenKeyboard Ltd. (2026)
// License: The Unlicense
//==============================================================

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

/// <summary>Reads borrowed bytes synchronously. Copy or decode them before returning if needed later.</summary>
/// <returns>A native reply, or null to continue the existing window's message processing.</returns>
public delegate nint? CopyDataCallback(nint senderHwnd, nuint dataId, ReadOnlySpan<byte> payload);

/// <summary>Attaches a WM_COPYDATA receiver to a caller-owned window on its owning thread.</summary>
public sealed unsafe class ReceiveCopyData : IDisposable
{
    private const uint WmCopyData = 0x004A;
    private const uint WmNcDestroy = 0x0082;
    private const nuint SubclassId = 1;
    // The native callback is static. Removing this root is safe even during a callback.
    private static readonly ConcurrentDictionary<nint, ReceiveCopyData> Receivers = new();
    private readonly Dictionary<nuint, CopyDataSubscription> _subscriptions = new();
    private readonly uint _threadId = Native.GetCurrentThreadId();
    private int _depth;
    private bool _disposed;

    /// <summary>Attaches one receiver per HWND. The caller retains the HWND and message loop.</summary>
    public ReceiveCopyData(nint hwnd, int maxNesting = 1)
    {
        uint owner = Native.GetWindowThreadProcessId(hwnd, out _);
        if (owner == 0)
            throw new ArgumentException("A valid window handle is required.", nameof(hwnd));
        if (owner != _threadId)
            throw new InvalidOperationException("Attach on the window's owning thread.");

        Hwnd = hwnd;
        MaxNesting = Magnitude(maxNesting);
        if (!Receivers.TryAdd(hwnd, this))
            throw new InvalidOperationException("A receiver is already attached to this window.");

        try
        {
            if (Native.SetWindowSubclass(hwnd, &WindowProcedure, SubclassId, 0) == 0)
                throw new InvalidOperationException("SetWindowSubclass failed.");
        }
        catch
        {
            _ = Receivers.TryRemove(new KeyValuePair<nint, ReceiveCopyData>(hwnd, this));
            throw;
        }
    }

    public nint Hwnd { get; }
    public int MaxNesting { get; private set; }

    /// <summary>Only raises the shared nesting limit. Like AHK, the sign is ignored; zero changes nothing.</summary>
    public void SetMaxNesting(int maxNesting)
    {
        CheckAccess();
        MaxNesting = Math.Max(MaxNesting, Magnitude(maxNesting));
    }

    /// <summary>Registers a callback, replacing and completing any previous registration for this ID.</summary>
    public CopyDataSubscription Register(nuint dataId, CopyDataCallback callback)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(callback);
        var subscription = new CopyDataSubscription(this, dataId, callback, _threadId);
        _subscriptions.TryGetValue(dataId, out CopyDataSubscription? previous);
        _subscriptions[dataId] = subscription;
        previous?.Retire();
        return subscription;
    }

    public bool Unregister(nuint dataId)
    {
        CheckAccess();
        if (!_subscriptions.Remove(dataId, out CopyDataSubscription? subscription))
            return false;
        subscription.Retire();
        return true;
    }

    internal void Remove(CopyDataSubscription subscription, Exception? error = null)
    {
        // A retired callback may finish or throw after it has registered its replacement.
        if (_subscriptions.TryGetValue(subscription.DataId, out CopyDataSubscription? current)
            && ReferenceEquals(current, subscription))
            _subscriptions.Remove(subscription.DataId);
        subscription.Retire(error);
    }

    private nint? Receive(nuint sender, nint parameter)
    {
        if (_disposed || _depth >= MaxNesting || parameter == 0)
            return null;

        Native.CopyData* data = (Native.CopyData*)parameter;
        if (!_subscriptions.TryGetValue(data->DataId, out CopyDataSubscription? subscription))
            return 1; // Original unregistered dwData response.

        // A span cannot represent these malformed/unrepresentable native payloads.
        if (data->ByteCount > int.MaxValue || (data->ByteCount != 0 && data->Data == null))
            return null;

        var payload = new ReadOnlySpan<byte>(data->Data, (int)data->ByteCount);
        CopyDataCallback callback = subscription.BeginInvocation();
        _depth++;
        try
        {
            return callback(unchecked((nint)sender), data->DataId, payload);
        }
        catch (Exception error)
        {
            Remove(subscription, error);
            return null;
        }
        finally
        {
            _depth--;
            subscription.EndInvocation();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData)
    {
        if (Receivers.TryGetValue(hwnd, out ReceiveCopyData? receiver))
        {
            if (message == WmNcDestroy)
            {
                // Do not throw across the native callback. The HWND is being destroyed;
                // the static callback remains safe even if native removal reports failure.
                _ = Native.RemoveWindowSubclass(hwnd, &WindowProcedure, SubclassId);
                receiver.CompleteDetach();
            }
            else if (message == WmCopyData && receiver.Receive(wParam, lParam) is nint reply)
                return reply;
        }
        return Native.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>Removes only this attachment. Never destroys the caller's window.</summary>
    public void Dispose()
    {
        CheckThread();
        if (_disposed)
            return;
        if (Native.RemoveWindowSubclass(Hwnd, &WindowProcedure, SubclassId) == 0)
            throw new InvalidOperationException("RemoveWindowSubclass failed; the receiver remains attached.");
        CompleteDetach();
    }

    private void CompleteDetach()
    {
        _disposed = true;
        _ = Receivers.TryRemove(new KeyValuePair<nint, ReceiveCopyData>(Hwnd, this));
        foreach (CopyDataSubscription subscription in _subscriptions.Values)
            subscription.Retire();
        _subscriptions.Clear();
    }

    private void CheckAccess()
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void CheckThread()
    {
        if (Native.GetCurrentThreadId() != _threadId)
            throw new InvalidOperationException("Use the receiver on the window's owning thread.");
    }

    private static int Magnitude(int value) => value == int.MinValue
        ? throw new ArgumentOutOfRangeException(nameof(value))
        : Math.Abs(value);
}
