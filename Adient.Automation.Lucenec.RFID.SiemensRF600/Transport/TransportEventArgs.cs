namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;

/// <summary>
/// Event args for connection state changes
/// </summary>
public class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets a value indicating whether the connection is currently established
    /// </summary>
    public bool IsConnected { get; }

    /// <summary>
    /// Gets the message describing the connection state change
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the timestamp when the state change occurred
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionStateChangedEventArgs"/> class
    /// </summary>
    /// <param name="isConnected">True if connected, false if disconnected</param>
    /// <param name="message">Description of the state change</param>
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
    /// <summary>
    /// Gets the type of asynchronous message
    /// </summary>
    public AsyncMessageType MessageType { get; }

    /// <summary>
    /// Gets the raw XML message content
    /// </summary>
    public string XmlMessage { get; }

    /// <summary>
    /// Gets the timestamp when the message was received
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncMessageReceivedEventArgs"/> class
    /// </summary>
    /// <param name="messageType">Type of the message</param>
    /// <param name="xmlMessage">Raw XML message content</param>
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
    /// <summary>
    /// Gets the diagnostic event name (e.g., "CommandSent", "ResponseReceived")
    /// </summary>
    public string EventName { get; }

    /// <summary>
    /// Gets the command ID associated with this event, if applicable
    /// </summary>
    public string? CommandId { get; }

    /// <summary>
    /// Gets the diagnostic message or data
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the timestamp when the event occurred
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticEventArgs"/> class
    /// </summary>
    /// <param name="eventName">Name of the diagnostic event</param>
    /// <param name="commandId">Associated command ID (optional)</param>
    /// <param name="message">Diagnostic message</param>
    public DiagnosticEventArgs(string eventName, string? commandId, string message)
    {
        EventName = eventName;
        CommandId = commandId;
        Message = message;
        Timestamp = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Types of asynchronous messages from the RFID reader
/// </summary>
public enum AsyncMessageType
{
    /// <summary>
    /// Report message (tag read reports, status reports)
    /// </summary>
    Report,

    /// <summary>
    /// Alarm message (error conditions, hardware failures)
    /// </summary>
    Alarm,

    /// <summary>
    /// Notification message (informational events)
    /// </summary>
    Notification
}