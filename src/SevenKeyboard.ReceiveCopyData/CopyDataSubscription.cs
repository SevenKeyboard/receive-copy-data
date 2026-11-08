namespace SevenKeyboard;

/// <summary>A dwData registration. Dispose it on the receiver's owning thread.</summary>
public sealed class CopyDataSubscription : IDisposable
{
    private ReceiveCopyData? _owner;
    private CopyDataCallback? _callback;
    private readonly uint _threadId;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeCalls;
    private bool _retired;
    private Exception? _error;

    internal CopyDataSubscription(ReceiveCopyData owner, nuint dataId, CopyDataCallback callback, uint threadId)
    {
        _owner = owner;
        DataId = dataId;
        _callback = callback;
        _threadId = threadId;
    }

    /// <summary>Completes after removal and any active invocations return. Callback errors fault this task.</summary>
    public Task Completion => _completion.Task;

    internal nuint DataId { get; }

    internal CopyDataCallback BeginInvocation()
    {
        _activeCalls++;
        return _callback!;
    }

    internal void EndInvocation()
    {
        _activeCalls--;
        CompleteIfRetired();
    }

    internal void Retire(Exception? error = null)
    {
        _retired = true;
        _error ??= error;
        _owner = null;
        _callback = null;
        CompleteIfRetired();
    }

    private void CompleteIfRetired()
    {
        if (!_retired || _activeCalls != 0)
            return;
        if (_error is { } error)
            _completion.TrySetException(error);
        else
            _completion.TrySetResult();
    }

    public void Dispose()
    {
        if (Native.GetCurrentThreadId() != _threadId)
            throw new InvalidOperationException("Dispose the subscription on the receiver's owning thread.");
        _owner?.Remove(this);
    }
}
