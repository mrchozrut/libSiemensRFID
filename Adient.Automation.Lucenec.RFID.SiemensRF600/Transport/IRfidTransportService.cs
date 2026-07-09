namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;

/// <summary>
/// Interface for RFID reader transport service
/// </summary>
public interface IRfidTransportService : IAsyncDisposable
{
    /// <summary>
    /// Gets the connection state
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets or sets the default command timeout
    /// </summary>
    TimeSpan DefaultTimeout { get; set; }

    /// <summary>
    /// Occurs when an asynchronous message is received (reports, alarms)
    /// </summary>
    event EventHandler<AsyncMessageReceivedEventArgs>? AsyncMessageReceived;

    /// <summary>
    /// Occurs when a connection state changes
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Occurs when diagnostic information is available
    /// </summary>
    event EventHandler<DiagnosticEventArgs>? DiagnosticEvent;

    /// <summary>
    /// Connect to the RFID reader
    /// </summary>
    Task ConnectAsync(TransportConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from the RFID reader
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a command and wait for response
    /// </summary>
    Task<string> SendCommandAsync(string xmlCommand, string commandId, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a command without waiting for response (fire and forget)
    /// </summary>
    Task SendCommandAsync(string xmlCommand, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel all pending commands
    /// </summary>
    Task CancelPendingCommandsAsync(CancellationToken cancellationToken = default);
}