using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;

/// <summary>
/// TCP/IP transport service for RFID readers
/// </summary>
public sealed class RfidTcpTransportService : IRfidTransportService
{
    private readonly ILogger<RfidTcpTransportService>? _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingCommand> _pendingCommands = new();
    private readonly CancellationTokenSource _receiveCts = new();

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private TransportConfiguration? _configuration;
    private Task? _receiveTask;
    private bool _isConnected;
    private int _sessionId;

    public bool IsConnected => _isConnected;
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public event EventHandler<AsyncMessageReceivedEventArgs>? AsyncMessageReceived;
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<DiagnosticEventArgs>? DiagnosticEvent;

    public RfidTcpTransportService(ILogger<RfidTcpTransportService>? logger = null)
    {
        _logger = logger;
    }

    #region Connection Management

    public async Task ConnectAsync(TransportConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_isConnected)
            {
                _logger?.LogWarning("Already connected to {Host}:{Port}", configuration.Host, configuration.Port);
                return;
            }

            _configuration = configuration;
            DefaultTimeout = configuration.DefaultTimeout;

            _logger?.LogInformation("Connecting to RFID reader at {Host}:{Port}", configuration.Host, configuration.Port);

            _tcpClient = new TcpClient
            {
                ReceiveBufferSize = configuration.ReceiveBufferSize,
                SendBufferSize = configuration.ReceiveBufferSize,
                NoDelay = true
            };

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(configuration.ConnectTimeout);

            try
            {
                await _tcpClient.ConnectAsync(configuration.Host, configuration.Port, connectCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Connection timeout after {configuration.ConnectTimeout.TotalSeconds}s");
            }

            _stream = _tcpClient.GetStream();
            _isConnected = true;
            _sessionId = 0;

            // Start receive loop
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), CancellationToken.None);

            _logger?.LogInformation("Successfully connected to {Host}:{Port}", configuration.Host, configuration.Port);
            OnConnectionStateChanged(true, "Connected");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to connect to {Host}:{Port}", configuration.Host, configuration.Port);
            CleanupResources();
            throw new RfidTransportException("Connection failed", ex);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (!_isConnected)
                return;

            _logger?.LogInformation("Disconnecting from RFID reader");

            // Cancel all pending commands
            await CancelPendingCommandsAsync(cancellationToken);

            // Stop receive loop
            _receiveCts.Cancel();

            // Wait for receive task to complete
            if (_receiveTask != null)
            {
                try
                {
                    await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (TimeoutException)
                {
                    _logger?.LogWarning("Receive task did not complete in time");
                }
            }

            CleanupResources();
            OnConnectionStateChanged(false, "Disconnected");

            _logger?.LogInformation("Disconnected from RFID reader");
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    #endregion

    #region Command Execution

    public async Task<string> SendCommandAsync(string xmlCommand, string commandId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        if (!_isConnected || _stream == null || _configuration == null)
            throw new InvalidOperationException("Not connected to RFID reader");

        var effectiveTimeout = timeout ?? DefaultTimeout;

        // Check concurrent command limit
        if (_pendingCommands.Count >= _configuration.MaxConcurrentCommands)
            throw new RfidTransportException($"Maximum concurrent commands ({_configuration.MaxConcurrentCommands}) reached");

        var pendingCommand = new PendingCommand(commandId);

        if (!_pendingCommands.TryAdd(commandId, pendingCommand))
            throw new RfidTransportException($"Command with ID {commandId} is already pending");

        try
        {
            // Send command
            await SendInternalAsync(xmlCommand, cancellationToken);

            LogDiagnostic("Command sent", commandId, xmlCommand);

            // Wait for response
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                var response = await pendingCommand.Task.WaitAsync(timeoutCts.Token);
                LogDiagnostic("Response received", commandId, response);
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Command {commandId} timed out after {effectiveTimeout.TotalSeconds}s");
            }
        }
        finally
        {
            _pendingCommands.TryRemove(commandId, out _);
        }
    }

    public async Task SendCommandAsync(string xmlCommand, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlCommand);

        if (!_isConnected || _stream == null)
            throw new InvalidOperationException("Not connected to RFID reader");

        await SendInternalAsync(xmlCommand, cancellationToken);
        LogDiagnostic("Fire-and-forget command sent", null, xmlCommand);
    }

    public async Task CancelPendingCommandsAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Cancelling {Count} pending commands", _pendingCommands.Count);

        // Increment session ID to invalidate all pending commands
        Interlocked.Increment(ref _sessionId);

        // Cancel all pending command tasks
        foreach (var (commandId, pendingCommand) in _pendingCommands)
        {
            pendingCommand.Cancel();
        }

        _pendingCommands.Clear();

        await Task.CompletedTask;
    }

    private async Task SendInternalAsync(string xmlCommand, CancellationToken cancellationToken)
    {
        if (_stream == null)
            throw new InvalidOperationException("Network stream is not available");

        var bytes = Encoding.UTF8.GetBytes(xmlCommand);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    #endregion

    #region Receive Loop

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[_configuration?.ReceiveBufferSize ?? 65536];
        var messageParser = new XmlMessageParser(_configuration?.MaxMessageSize ?? 10 * 1024 * 1024);

        _logger?.LogDebug("Receive loop started");

        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream != null)
            {
                var bytesRead = await _stream.ReadAsync(buffer, cancellationToken);

                if (bytesRead == 0)
                {
                    _logger?.LogWarning("Connection closed by remote host");
                    break;
                }

                var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                messageParser.AppendData(data);

                // Process all complete messages
                while (messageParser.TryExtractMessage(out var message, out var messageType))
                {
                    await ProcessReceivedMessageAsync(message, messageType);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Receive loop cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in receive loop");
            OnConnectionStateChanged(false, $"Connection error: {ex.Message}");
            _isConnected = false;
        }
        finally
        {
            _logger?.LogDebug("Receive loop ended");
        }
    }

    private async Task ProcessReceivedMessageAsync(string message, XmlMessageType messageType)
    {
        try
        {
            switch (messageType)
            {
                case XmlMessageType.Reply:
                    ProcessCommandReply(message);
                    break;

                case XmlMessageType.Report:
                    OnAsyncMessageReceived(AsyncMessageType.Report, message);
                    await AcknowledgeAsyncMessageIfRequiredAsync(message, "report");
                    break;

                case XmlMessageType.Alarm:
                    OnAsyncMessageReceived(AsyncMessageType.Alarm, message);
                    await AcknowledgeAsyncMessageIfRequiredAsync(message, "alarm");
                    break;

                case XmlMessageType.Notification:
                    OnAsyncMessageReceived(AsyncMessageType.Notification, message);
                    await AcknowledgeAsyncMessageIfRequiredAsync(message, "notification");
                    break;

                default:
                    _logger?.LogWarning("Unknown message type received");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing received message");
        }
    }

    private void ProcessCommandReply(string message)
    {
        var commandId = ExtractCommandId(message);

        if (string.IsNullOrEmpty(commandId))
        {
            _logger?.LogWarning("Received reply without command ID");
            return;
        }

        if (_pendingCommands.TryRemove(commandId, out var pendingCommand))
        {
            pendingCommand.SetResult(message);
            _logger?.LogDebug("Reply matched to command {CommandId}", commandId);
        }
        else
        {
            _logger?.LogWarning("Received reply for unknown command {CommandId}", commandId);
        }
    }

    private async Task AcknowledgeAsyncMessageIfRequiredAsync(string message, string messageType)
    {
        if (_configuration?.AcknowledgeAsyncMessages != true)
            return;

        try
        {
            var id = ExtractMessageId(message);
            if (!string.IsNullOrEmpty(id))
            {
                var ackXml = $"<frame><ack><id>{id}</id></{messageType}Ack></ack></frame>";
                await SendCommandAsync(ackXml);
                LogDiagnostic("Acknowledgment sent", id, ackXml);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to acknowledge async message");
        }
    }

    #endregion

    #region XML Parsing Helpers

    private static string ExtractCommandId(string xml)
    {
        const string idStart = "<id>";
        const string idEnd = "</id>";

        var startIndex = xml.IndexOf(idStart, StringComparison.Ordinal);
        if (startIndex == -1)
            return string.Empty;

        startIndex += idStart.Length;
        var endIndex = xml.IndexOf(idEnd, startIndex, StringComparison.Ordinal);

        return endIndex == -1 ? string.Empty : xml.Substring(startIndex, endIndex - startIndex);
    }

    private static string ExtractMessageId(string xml)
    {
        // Same logic as ExtractCommandId for now
        return ExtractCommandId(xml);
    }

    #endregion

    #region Event Raising

    private void OnConnectionStateChanged(bool isConnected, string message)
    {
        _isConnected = isConnected;
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(isConnected, message));
    }

    private void OnAsyncMessageReceived(AsyncMessageType messageType, string message)
    {
        AsyncMessageReceived?.Invoke(this, new AsyncMessageReceivedEventArgs(messageType, message));
    }

    private void LogDiagnostic(string eventName, string? commandId, string message)
    {
        if (_configuration?.EnableDiagnostics == true)
        {
            DiagnosticEvent?.Invoke(this, new DiagnosticEventArgs(eventName, commandId, message));
        }
    }

    #endregion

    #region Cleanup

    private void CleanupResources()
    {
        _isConnected = false;

        try { _stream?.Close(); } catch { /* Ignore */ }
        try { _tcpClient?.Close(); } catch { /* Ignore */ }

        _stream = null;
        _tcpClient = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _connectionLock.Dispose();
        _sendLock.Dispose();
        _receiveCts.Dispose();
    }

    #endregion
}