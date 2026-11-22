using Microsoft.UI.Dispatching;
using Temno.Core.Session;

namespace Temno.App.Services;

public sealed class WinUiProjectSessionDispatcher : IProjectSessionDispatcher
{
    private readonly DispatcherQueue _queue;

    public WinUiProjectSessionDispatcher(DispatcherQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public T Invoke<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (HasThreadAccess)
            return operation();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryEnqueue(() =>
            {
                try
                {
                    completion.TrySetResult(operation());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            throw new InvalidOperationException("Temno's project-session dispatcher is unavailable.");
        }

        return completion.Task.GetAwaiter().GetResult();
    }
}
