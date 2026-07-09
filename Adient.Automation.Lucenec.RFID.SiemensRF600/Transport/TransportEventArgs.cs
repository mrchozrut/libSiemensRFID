namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;

/// <summary>
/// Event args for connection state changes
/// </summary>
public class ConnectionStateChangedEventArgs : EventArgs
{
    public bool IsConnected { get; }
    public string Message { get; }
    public DateTimeOffset Timestamp { get; }

    public ConnectionStateChangedEventArgs(bool isConnected, string message)
    {
        IsConnected = isConnected;
        Message = message;
        Timestamp = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Event args for asynchronous messages (reports, alarms, notifications)
/// </summary>
public class AsyncMessageReceivedEventArgs : EventArgs
{
    public AsyncMessageType MessageType { get; }
    public string XmlMessage { get; }
    public DateTimeOffset Timestamp { get; }

    public AsyncMessageReceivedEventArgs(AsyncMessageType messageType, string xmlMessage)
    {
        MessageType = messageType;
        XmlMessage = xmlMessage;
        Timestamp = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Event args for diagnostic events
/// </summary>
public class DiagnosticEventArgs : EventArgs
{
    public string EventName { get; }
    public string? CommandId { get; }
    public string Message { get; }
    public DateTimeOffset Timestamp { get; }

    public DiagnosticEventArgs(string eventName, string? commandId, string message)
    {
        EventName = eventName;
        CommandId = commandId;
        Message = message;
        Timestamp = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Types of asynchronous messages
/// </summary>
public enum AsyncMessageType
{
    Report,
    Alarm,
    Notification
}