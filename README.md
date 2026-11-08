# ReceiveCopyData

WM_COPYDATA dispatch for .NET 10 on Windows. Version **2.0.0**, namespace
`SevenKeyboard`, compatible with NativeAOT x64/x86.

```csharp
using SevenKeyboard;
using System.Text;

// Create, register, and dispose on the thread that owns hwnd.
using var receiver = new ReceiveCopyData(hwnd);
using var subscription = receiver.Register(0x545346, (senderHwnd, dataId, bytes) =>
{
    string text = Encoding.Unicode.GetString(bytes);
    // Interpret terminators and message contents here.
    // Use bytes.ToArray() here if raw bytes are needed after this callback.
    return 1;
});

receiver.SetMaxNesting(8); // 1 -> 8 -> 5 stays 8.
// Keep the receiver alive while the caller's existing message loop runs.
```

The receiver attaches directly to the supplied HWND. It creates no window or
worker thread and needs no UI framework or product message forwarding. Only one
receiver may attach to an HWND. Dispose removes its attachment without destroying
the window; destruction of that window also releases the receiver.

Callbacks run synchronously on the window's owning thread. The span borrows the
message's original bytes, with no automatic copy, decoding, or terminator removal.
It is valid only during the callback. Metadata retains native pointer width. The sender is the native HWND; the AHK
runtime-specific x64 wParam mask to 32 bits is not reproduced.
A non-null `nint?` reply handles the message, including 0; null continues the
existing window's message processing. An unregistered data ID returns 1.

`Register(dataId, callback)` replaces the previous registration for that ID.
Dispose the returned `CopyDataSubscription`, or call `Unregister(dataId)`, to
remove it. Disposing an old registration does not remove its replacement.
Its `Completion` finishes after removal and any active callback returns. An
unhandled callback exception removes only that registration, faults its
`Completion`, and chains the current message; other registrations remain active.
Observe `Completion` to handle callback failures. Do not synchronously await a
pending completion on the window thread.

`MaxNesting` is shared across all IDs on this receiver, defaults to 1, and only
increases through `SetMaxNesting`. Signed limits use their magnitude; this preserves the nesting count only, not
AHK's negative OnMessage callback priority;
zero does not raise the limit. An attachment created with zero chains all messages
until its limit is raised. Overflow chains immediately without queuing callbacks.
Attachment, registration changes, limit changes, and disposal require the owner
thread; `Completion` can be observed from any thread.

A null COPYDATASTRUCT pointer, a nonempty null payload, or a registered payload
larger than a span can represent is chained without invoking the callback.
Senders must otherwise supply valid native WM_COPYDATA data. Version 2.0.0 replaces
AHK's global script attachment and raw pointers with explicit HWND/subscription
lifetimes and borrowed spans.

See [build and smoke checks](tests/README.md).

Source: this repository's original AHK1.1 branch. License: The Unlicense.
