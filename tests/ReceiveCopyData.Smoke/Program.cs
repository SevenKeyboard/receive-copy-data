using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SevenKeyboard;

internal static class Program
{
    private static int _checks;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--require-aot"))
                Check(!RuntimeFeature.IsDynamicCodeSupported, "NativeAOT required");
            PayloadAndReplies();
            ReplacementAndErrors();
            Nesting();
            LifetimeDuringCallbacks();
            ThreadsAndDestruction();
            Console.WriteLine($"PASS: {_checks} ReceiveCopyData checks; {RuntimeInformation.ProcessArchitecture}, dynamic={RuntimeFeature.IsDynamicCodeSupported}.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static unsafe void PayloadAndReplies()
    {
        Check(sizeof(Native.CopyData) == (nint.Size == 8 ? 24 : 12), "COPYDATASTRUCT layout");
        using var window = new TestWindow();
        using var sender = new TestWindow();
        Check(Native.IsWindowVisible(window.Hwnd) == 0, "owned receiver stays hidden");
        using var receiver = new ReceiveCopyData(window.Hwnd);
        Throws<ArgumentException>(() => new ReceiveCopyData(0), "invalid HWND rejected");
        Throws<InvalidOperationException>(() => new ReceiveCopyData(window.Hwnd), "duplicate receiver rejected");
        Check(receiver.Hwnd == window.Hwnd && receiver.MaxNesting == 1, "receiver metadata/default limit");

        nuint id = nint.Size == 8 ? unchecked((nuint)0x1234567800000055UL) : (nuint)0x80000055;
        byte[] bytes = [0, 65, 0, 0xFF, 42];
        byte[]? retained = null;
        nint expectedAddress = 0;
        nint reply = -17;
        uint ownerThread = Native.GetCurrentThreadId();
        using var registration = receiver.Register(id, (from, receivedId, payload) =>
        {
            Check(from == sender.Hwnd && receivedId == id, "sender HWND and full-width data ID");
            Check(Native.GetCurrentThreadId() == ownerThread, "synchronous owner-thread callback");
            fixed (byte* address = payload)
                Check((nint)address == expectedAddress, "payload is borrowed without a copy");
            retained = payload.ToArray();
            return reply;
        });
        fixed (byte* address = bytes)
        {
            expectedAddress = (nint)address;
            Check(Native.Send(window.Hwnd, id, bytes, sender.Hwnd) == -17, "negative reply preserved");
            reply = 0;
            Check(Native.Send(window.Hwnd, id, bytes, sender.Hwnd) == 0, "zero is handled");
            if (nint.Size == 8)
            {
                reply = unchecked((nint)0x1234567800000001L);
                Check(Native.Send(window.Hwnd, id, bytes, sender.Hwnd) == reply, "full-width reply preserved");
            }
        }
        Check(retained!.SequenceEqual(bytes), "optional copy retains embedded zero and binary bytes");
        Check(window.CopyDataCalls == 0, "handled replies do not reach original procedure");
        Check(Native.Send(window.Hwnd, 999, bytes) == 1, "unknown ID replies 1");
        Check(Native.SendMessage(window.Hwnd, TestWindow.OtherMessage, 0, 0) == 719, "unrelated message preserved");

        using var chain = receiver.Register(2, (_, _, _) => null);
        Check(Native.Send(window.Hwnd, 2, bytes) == TestWindow.DefaultReply, "null chains to original procedure");
        Check(window.CopyDataCalls == 1, "original procedure called once");
        int emptyCalls = 0;
        using var empty = receiver.Register(3, (_, _, payload) => { emptyCalls++; Check(payload.IsEmpty, "empty payload"); return 1; });
        Check(Native.Send(window.Hwnd, 3, []) == 1 && emptyCalls == 1, "empty payload allowed");
        Native.CopyData invalid = new() { DataId = 3, ByteCount = 1, Data = null };
        Check(Native.SendMessage(window.Hwnd, Native.WmCopyData, 0, (nint)(&invalid)) == TestWindow.DefaultReply,
            "nonempty null payload chains");
        invalid.ByteCount = uint.MaxValue;
        Check(Native.SendMessage(window.Hwnd, Native.WmCopyData, 0, (nint)(&invalid)) == TestWindow.DefaultReply,
            "unrepresentable payload chains");
        Check(Native.SendMessage(window.Hwnd, Native.WmCopyData, 0, 0) == TestWindow.DefaultReply,
            "null COPYDATASTRUCT chains");
        Check(emptyCalls == 1 && !empty.Completion.IsCompleted, "invalid payload leaves subscription active");
    }

    private static void ReplacementAndErrors()
    {
        using var window = new TestWindow();
        using var receiver = new ReceiveCopyData(window.Hwnd);
        using var old = receiver.Register(1, (_, _, _) => 11);
        using var replacement = receiver.Register(1, (_, _, _) => 22);
        Check(old.Completion.IsCompletedSuccessfully, "replacement completes retired subscription");
        old.Dispose();
        Check(Native.Send(window.Hwnd, 1, []) == 22, "old token cannot remove replacement");
        Check(receiver.Unregister(1) && !receiver.Unregister(1), "unregister reports presence");
        Check(replacement.Completion.IsCompletedSuccessfully, "explicit unregister completes");
        Check(Native.Send(window.Hwnd, 1, []) == 1, "removed ID uses original unknown response");

        var failure = new InvalidOperationException("callback fixture");
        using var bad = receiver.Register(2, (_, _, _) => throw failure);
        using var good = receiver.Register(3, (_, _, _) => 44);
        Check(Native.Send(window.Hwnd, 2, []) == TestWindow.DefaultReply, "callback exception chains current message");
        Check(bad.Completion.IsFaulted, "callback error faults Completion");
        Check(ReferenceEquals(ObserveError(bad.Completion), failure), "original exception retained");
        Check(Native.Send(window.Hwnd, 3, []) == 44 && !good.Completion.IsCompleted, "other registrations survive");
        Check(Native.Send(window.Hwnd, 2, []) == 1, "failed subscription removed");
    }

    private static void Nesting()
    {
        using var window = new TestWindow();
        using var receiver = new ReceiveCopyData(window.Hwnd);
        int secondCalls = 0;
        nint nestedReply = 0;
        using var first = receiver.Register(1, (_, _, _) => { nestedReply = Native.Send(window.Hwnd, 2, []); return 91; });
        using var second = receiver.Register(2, (_, _, _) => { secondCalls++; return 92; });
        Check(Native.Send(window.Hwnd, 1, []) == 91, "outer callback completes");
        Check(secondCalls == 0 && nestedReply == TestWindow.DefaultReply, "default limit shared across IDs; overflow chains");
        receiver.SetMaxNesting(8);
        receiver.SetMaxNesting(5);
        receiver.SetMaxNesting(0);
        Check(receiver.MaxNesting == 8, "1 -> 8 -> 5 -> 0 stays 8");
        Check(Native.Send(window.Hwnd, 1, []) == 91 && secondCalls == 1 && nestedReply == 92, "raised limit admits second ID");

        int calls = 0;
        nint overflowReply = 0;
        using var recursive = receiver.Register(3, (_, _, _) =>
        {
            calls++;
            nint value = Native.Send(window.Hwnd, 3, []);
            if (value == TestWindow.DefaultReply)
                overflowReply = value;
            return 93;
        });
        Check(Native.Send(window.Hwnd, 3, []) == 93 && calls == 8, "real sent-message recursion stops at shared limit");
        Check(overflowReply == TestWindow.DefaultReply, "overflow original response retained");
        calls = 0;
        Check(Native.Send(window.Hwnd, 3, []) == 93 && calls == 8, "nesting restored after unwind");

        receiver.SetMaxNesting(-9);
        Check(receiver.MaxNesting == 9, "AHK magnitude semantics");
        Throws<ArgumentOutOfRangeException>(() => receiver.SetMaxNesting(int.MinValue), "unrepresentable limit rejected");

        using var zeroWindow = new TestWindow();
        using var zero = new ReceiveCopyData(zeroWindow.Hwnd, 0);
        int zeroCalls = 0;
        using var zeroSubscription = zero.Register(1, (_, _, _) => { zeroCalls++; return 1; });
        Check(Native.Send(zeroWindow.Hwnd, 1, []) == TestWindow.DefaultReply && zeroCalls == 0, "zero initial limit chains");
        zero.SetMaxNesting(1);
        Check(Native.Send(zeroWindow.Hwnd, 1, []) == 1 && zeroCalls == 1, "zero initial limit can be raised");
    }

    private static void LifetimeDuringCallbacks()
    {
        using var window = new TestWindow();
        using var receiver = new ReceiveCopyData(window.Hwnd);
        CopyDataSubscription? current = null, next = null;
        var failure = new InvalidOperationException("retired active callback");
        current = receiver.Register(1, (_, _, _) =>
        {
            next = receiver.Register(1, (_, _, _) => 73);
            Check(!current!.Completion.IsCompleted, "active replaced subscription has not completed");
            throw failure;
        });
        Check(Native.Send(window.Hwnd, 1, []) == TestWindow.DefaultReply, "replaced callback failure chains");
        Check(ReferenceEquals(ObserveError(current.Completion), failure), "replacement cannot hide active callback failure");
        current.Dispose();
        Check(Native.Send(window.Hwnd, 1, []) == 73 && !next!.Completion.IsCompleted, "failing retired callback keeps replacement");
        next!.Dispose();

        CopyDataSubscription? self = null;
        self = receiver.Register(2, (_, _, _) =>
        {
            self!.Dispose();
            Check(!self.Completion.IsCompleted, "self-disposal waits for callback return");
            return 29;
        });
        Check(Native.Send(window.Hwnd, 2, []) == 29 && self.Completion.IsCompletedSuccessfully, "self-disposal finishes after return");

        CopyDataSubscription? detaching = null;
        detaching = receiver.Register(3, (_, _, _) =>
        {
            receiver.Dispose();
            Check(!detaching!.Completion.IsCompleted, "receiver disposal waits for active callback");
            Check(Native.IsWindow(window.Hwnd) != 0, "receiver disposal does not destroy HWND");
            return null;
        });
        Check(Native.Send(window.Hwnd, 3, []) == TestWindow.DefaultReply, "disposal during callback still chains");
        Check(detaching.Completion.IsCompletedSuccessfully, "active callback completed after detach");
        Check(Native.Send(window.Hwnd, 999, []) == TestWindow.DefaultReply, "detached receiver no longer handles unknown IDs");
        using var reattached = new ReceiveCopyData(window.Hwnd);
        Check(Native.Send(window.Hwnd, 999, []) == 1, "same HWND can be reattached");

        using var destroyed = new TestWindow();
        using var dying = new ReceiveCopyData(destroyed.Hwnd);
        CopyDataSubscription? destroying = null;
        destroying = dying.Register(1, (_, _, _) =>
        {
            destroyed.Destroy();
            Check(!destroying!.Completion.IsCompleted, "NCDESTROY retains active callback lifetime");
            return 77;
        });
        nint target = destroyed.Hwnd;
        Check(Native.Send(target, 1, []) == 77, "callback can finish after HWND destruction");
        Check(destroying.Completion.IsCompletedSuccessfully, "NCDESTROY completes active subscription after return");
        Throws<ObjectDisposedException>(() => dying.Register(2, (_, _, _) => 1), "destroyed receiver rejects registration");
    }

    private static void ThreadsAndDestruction()
    {
        using var window = new TestWindow();
        using var sender = new TestWindow();
        using var receiver = new ReceiveCopyData(window.Hwnd);
        uint owner = Native.GetCurrentThreadId();
        using var subscription = receiver.Register(4, (from, _, bytes) =>
        {
            Check(Native.GetCurrentThreadId() == owner && from == sender.Hwnd, "cross-thread send dispatches on HWND owner");
            Check(bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "cross-thread bytes");
            return 81;
        });

        Task.Run(() =>
        {
            Throws<InvalidOperationException>(() => new ReceiveCopyData(window.Hwnd), "foreign-thread attachment rejected");
            Throws<InvalidOperationException>(() => receiver.Register(2, (_, _, _) => 1), "foreign-thread registration rejected");
            Throws<InvalidOperationException>(() => receiver.SetMaxNesting(8), "foreign-thread limit change rejected");
            Throws<InvalidOperationException>(receiver.Dispose, "foreign-thread detach rejected");
            Throws<InvalidOperationException>(subscription.Dispose, "foreign-thread subscription disposal rejected");
        }).GetAwaiter().GetResult();

        Task<nint> send = Task.Run(() => Native.SendTimed(window.Hwnd, 4, [1, 2, 3], sender.Hwnd));
        var timer = Stopwatch.StartNew();
        while (!send.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            while (Native.PeekMessage(out Native.Message message, 0, 0, 0, 1) != 0)
                _ = Native.DispatchMessage(in message);
            Thread.Sleep(1);
        }
        Check(send.IsCompleted && send.GetAwaiter().GetResult() == 81, "bounded cross-thread native send");
        nint hwnd = window.Hwnd;
        window.Destroy();
        Check(Native.IsWindow(hwnd) == 0 && subscription.Completion.IsCompletedSuccessfully, "external destruction completes subscriptions");
        receiver.Dispose();

        using var retainedWindow = new TestWindow();
        WeakReference weak = AttachAndDispose(retainedWindow.Hwnd);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check(!weak.IsAlive && Native.IsWindow(retainedWindow.Hwnd) != 0, "detachment releases root without destroying HWND");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndDispose(nint hwnd)
    {
        var receiver = new ReceiveCopyData(hwnd);
        var weak = new WeakReference(receiver);
        receiver.Dispose();
        return weak;
    }

    private static Exception ObserveError(Task completion)
    {
        try { completion.GetAwaiter().GetResult(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected callback failure.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
        Interlocked.Increment(ref _checks);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, message); return; }
        throw new InvalidOperationException(message);
    }
}
