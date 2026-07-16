namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;

/// <summary>
/// Configuration for RFID transport service
/// </summary>
public record TransportConfiguration
{
    /// <summary>
    /// IP address or hostname of the RFID reader
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// TCP port number (default: 10001)
    /// </summary>
    public int Port { get; init; } = 10001;

    /// <summary>
    /// Maximum number of concurrent pending commands
    /// </summary>
    public int MaxConcurrentCommands { get; init; } = 10;

    /// <summary>
    /// Default timeout for commands
    /// </summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout for long-running commands (e.g., configuration operations)
    /// </summary>
    public TimeSpan LongTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Maximum message size in bytes (default: 10MB)
    /// </summary>
    public int MaxMessageSize { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Whether to acknowledge asynchronous messages (reports, alarms)
    /// </summary>
    public bool AcknowledgeAsyncMessages { get; init; }

    /// <summary>
    /// Enable diagnostic logging
    /// </summary>
    public bool EnableDiagnostics { get; init; }

    /// <summary>
    /// Receive buffer size in bytes
    /// </summary>
    public int ReceiveBufferSize { get; init; } = 65536;

    /// <summary>
    /// Connection timeout
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reconnection strategy
    /// </summary>
    public ReconnectionStrategy ReconnectionStrategy { get; init; } = ReconnectionStrategy.None;

    /// <summary>
    /// Validate configuration
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("Host cannot be null or empty", nameof(Host));

        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), "Port must be between 1 and 65535");

        if (MaxConcurrentCommands < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentCommands), "MaxConcurrentCommands must be at least 1");

        if (DefaultTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(DefaultTimeout), "DefaultTimeout must be positive");

        if (MaxMessageSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxMessageSize), "MaxMessageSize must be at least 1024 bytes");
    }
}

/// <summary>
/// Defines the reconnection strategy when connection is lost
/// </summary>
public enum ReconnectionStrategy
{
    /// <summary>
    /// No automatic reconnection - manual reconnection required
    /// </summary>
    None,

    /// <summary>
    /// Automatic reconnection with exponential backoff
    /// </summary>
    Automatic,

    /// <summary>
    /// Only manual reconnection is allowed
    /// </summary>
    ManualOnly
}