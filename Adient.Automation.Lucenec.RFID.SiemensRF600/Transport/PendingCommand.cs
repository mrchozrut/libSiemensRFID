namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;

/// <summary>
/// Represents a command waiting for response
/// </summary>
internal sealed class PendingCommand
{
    private readonly TaskCompletionSource<string> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CommandId { get; }
    public Task<string> Task => _tcs.Task;

    public PendingCommand(string commandId)
    {
        CommandId = commandId;
    }

    public void SetResult(string response)
    {
        _tcs.TrySetResult(response);
    }

    public void Cancel()
    {
        _tcs.TrySetCanceled();
    }

    public void SetException(Exception exception)
    {
        _tcs.TrySetException(exception);
    }
}