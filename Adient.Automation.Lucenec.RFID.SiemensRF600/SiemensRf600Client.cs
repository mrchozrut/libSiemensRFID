using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace Adient.Automation.Lucenec.RFID.SiemensRF600;

/// <summary>
/// Modern async client for Siemens RF600 RFID readers
/// </summary>
public class SiemensRf600Client : IDisposable, IAsyncDisposable
{
    private readonly string _ipAddress;
    private readonly int _port;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private bool _isConnected;
    private int _commandId;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly List<CommandReply> _pendingReplies = new();
    private Task? _receiveTask;

    /// <summary>
    /// Gets a value indicating whether the client is currently connected to the RFID reader
    /// </summary>
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Gets or sets the default timeout for standard RFID operations
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the timeout for long-running operations (e.g., configuration changes, firmware updates)
    /// </summary>
    public TimeSpan LongTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Initializes a new instance of the <see cref="SiemensRf600Client"/> class
    /// </summary>
    /// <param name="ipAddress">IP address of the RFID reader</param>
    /// <param name="port">TCP port number (default is 10001)</param>
    public SiemensRf600Client(string ipAddress, int port = 10001)
    {
        _ipAddress = ipAddress;
        _port = port;
        _commandId = 1;
    }

    #region Connection Management

    /// <summary>
    /// Establish connection to the RFID reader
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the connection operation</param>
    /// <returns>True if connected successfully, false otherwise</returns>
    /// <remarks>
    /// ? This method has been tested and validated with physical hardware.
    /// The response parser is fully functional.
    /// </remarks>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isConnected)
            return true;

        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(IPAddress.Parse(_ipAddress), _port, cancellationToken);
            _stream = _tcpClient.GetStream();
            _isConnected = true;

            // Start receive loop
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), cancellationToken);

            return true;
        }
        catch
        {
            _isConnected = false;
            return false;
        }
    }

    /// <summary>
    /// Disconnect from the RFID reader and clean up resources
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the disconnection operation</param>
    /// <remarks>
    /// ? This method has been tested and validated with physical hardware.
    /// The response parser is fully functional.
    /// </remarks>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConnected)
            return;

        try
        {
            // Send goodbye command
            await HostGoodbyeAsync("Default", cancellationToken);
        }
        catch { /* Ignore errors during disconnect */ }
        finally
        {
            await _receiveCts.CancelAsync();
            _isConnected = false;
            _stream?.Close();
            _tcpClient?.Close();
        }
    }

    /// <summary>
    /// Send host greetings command (should be first command after connection)
    /// </summary>
    /// <param name="readerType">Type of reader (e.g., "RF600", "RF680R")</param>
    /// <param name="supportedVersions">Array of protocol versions supported by the host application</param>
    /// <param name="readerMode">Operating mode (default is "Default")</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Configuration ID and version information from the reader</returns>
    /// <remarks>
    /// ? This method has been tested and validated with physical hardware.
    /// The response parser is fully functional and correctly extracts version and configuration ID.
    /// </remarks>
    public async Task<RfConfigId> HostGreetingsAsync(string readerType, string[] supportedVersions, string readerMode = "Default", CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("readerType", readerType),
            new("readerMode", readerMode),
            new("supportedVersions", Value: "", IsContainer: true, IsEmpty: false)
        };

        foreach (var versionInfo in supportedVersions)
        {
            parameters.Add(new XmlParameter("version", versionInfo));
        }
        
        // Mark the last version as the last child of supportedVersions container
        if (supportedVersions.Length > 0)
        {
            var lastIndex = parameters.Count - 1;
            parameters[lastIndex] = parameters[lastIndex] with { IsLastChild = true };
        }

        var reply = await ExecuteCommandAsync("hostGreetings", parameters, LongTimeout, cancellationToken); // FIXED: was cancelationToken

        var version = GetParameter(reply.Parameters, "version");
        var configId = GetParameter(reply.Parameters, "configID");

        return new RfConfigId { Version = version, ConfigID = configId };
    }

    /// <summary>
    /// Send host goodbye command (should be last command before disconnection)
    /// </summary>
    /// <param name="readerMode">Operating mode (default is "Default")</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <remarks>
    /// ? This method has been tested and validated with physical hardware.
    /// The response parser is fully functional.
    /// </remarks>
    public async Task HostGoodbyeAsync(string readerMode = "Default", CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("readerMode", readerMode) };
        await ExecuteCommandAsync("hostGoodbye", parameters, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Check if reader is present and responsive (heartbeat)
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task HeartBeatAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteCommandAsync("heartBeat", null, DefaultTimeout, cancellationToken);
    }

    #endregion

    #region Reader Control

    /// <summary>
    /// Start the reader (leave stop mode and begin reading tags)
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task StartReaderAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteCommandAsync("startReader", new List<XmlParameter>(), DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Stop the reader (enter stop mode and cease tag reading)
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task StopReaderAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteCommandAsync("stopReader", new List<XmlParameter>(), DefaultTimeout, cancellationToken);
    }

    #endregion

    #region Tag Inventory Operations

    /// <summary>
    /// Read tag IDs from specified source antenna or antenna group
    /// </summary>
    /// <param name="sourceName">Name of the antenna source (e.g., "Antenna1", "AntennaGroup1")</param>
    /// <param name="durationMs">Duration in milliseconds (if unit is "Time") or number of inventory rounds (if unit is "InventoryRounds")</param>
    /// <param name="unit">Unit type: "Time" for milliseconds or "InventoryRounds" for read cycles</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array of detected RFID tags</returns>
    /// <remarks>
    /// ? This method has been tested and validated with physical hardware.
    /// The response parser is fully functional and correctly extracts tag IDs, RSSI, antenna names, and timestamps.
    /// </remarks>
    public async Task<RfTag[]> ReadTagIdsAsync(string sourceName, uint durationMs, string unit = "Time", CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("sourceName", sourceName),
            new("duration", durationMs.ToString()),
            new("unit", unit)
        };

        var timeout = unit == "Time" ? DefaultTimeout.Add(TimeSpan.FromMilliseconds(durationMs)) : DefaultTimeout.Add(TimeSpan.FromSeconds(durationMs));
        var reply = await ExecuteCommandAsync("readTagIDs", parameters, timeout, cancellationToken);

        return reply.TagData ?? Array.Empty<RfTag>();
    }

    /// <summary>
    /// Get observed tag IDs using reader's smoothing algorithm to filter out transient reads
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="durationMs">Duration in milliseconds or inventory rounds (default: 1000)</param>
    /// <param name="unit">Unit type: "Time" or "InventoryRounds"</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array of observed RFID tags after smoothing</returns>
    public async Task<RfTag[]> GetObservedTagIdsAsync(string sourceName, uint durationMs = 1000, string unit = "Time", CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("sourceName", sourceName),
            new("duration", durationMs.ToString()),
            new("unit", unit)
        };

        var timeout = unit == "Time" ? DefaultTimeout.Add(TimeSpan.FromMilliseconds(durationMs)) : DefaultTimeout.Add(TimeSpan.FromSeconds(durationMs * 8));
        var reply = await ExecuteCommandAsync("getObservedTagIDs", parameters, timeout, cancellationToken);

        return reply.TagData ?? Array.Empty<RfTag>();
    }

    /// <summary>
    /// Trigger source antenna for one reading cycle
    /// </summary>
    /// <param name="sourceName">Name of the antenna source to trigger</param>
    /// <param name="mode">Trigger mode: "Single" for one cycle or "Continuous" for ongoing reads</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task TriggerSourceAsync(string sourceName, string mode = "Single", CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("sourceName", sourceName),
            new("triggerMode", mode)
        };

        await ExecuteCommandAsync("triggerSource", parameters, DefaultTimeout, cancellationToken);
    }

    #endregion

    #region Tag Memory Operations

    /// <summary>
    /// Write a new EPC ID to an RFID tag
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="currentTagId">Current tag ID to identify which tag to write (can be empty to write any tag)</param>
    /// <param name="newTagId">New tag ID to write in hexadecimal format</param>
    /// <param name="idLength">Length of the new ID in words (0 = auto-detect)</param>
    /// <param name="password">Access password for the tag (empty if not required)</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array of tags that were written</returns>
    public async Task<RfTag[]> WriteTagIdAsync(string sourceName, string currentTagId, string newTagId, uint idLength = 0, string password = "", CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>();

        if (!string.IsNullOrEmpty(sourceName))
            parameters.Add(new XmlParameter("sourceName", sourceName));

        if (!string.IsNullOrEmpty(currentTagId))
            parameters.Add(new("tagID", currentTagId));

        parameters.Add(new("newID", newTagId));

        if (idLength > 0)
            parameters.Add(new("idLength", idLength.ToString()));

        if (!string.IsNullOrEmpty(password))
            parameters.Add(new("password", password));

        var reply = await ExecuteCommandAsync("writeTagID", parameters, DefaultTimeout, cancellationToken);
        return reply.TagData ?? Array.Empty<RfTag>();
    }

    /// <summary>
    /// Read tag memory from specific memory banks by bank number, address, and length
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="tagId">Tag ID to read (empty to read any tag in field)</param>
    /// <param name="password">Access password for the tag (empty if not required)</param>
    /// <param name="tagFields">Array of memory fields specifying bank, address, and length to read</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array of tags with memory data populated</returns>
    /// <remarks>
    /// ? This method has been tested and validated with physical hardware.
    /// The response parser is fully functional and correctly extracts data from EPC, TID, and USER memory banks.
    /// Successfully tested with reading EPC (bank 1) and TID (bank 2) memory regions.
    /// </remarks>
    public async Task<RfTag[]> ReadTagMemoryAsync(string sourceName, string tagId, string password, RfTagField[] tagFields, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("sourceName", sourceName)
        };

        if (!string.IsNullOrEmpty(tagId))
            parameters.Add(new("tagID", tagId));

        if (!string.IsNullOrEmpty(password))
            parameters.Add(new("password", password));

        foreach (var field in tagFields)
        {
            parameters.Add(new("tagField"));
            
            // Convert bank number to string for XML
            parameters.Add(new("bank", field.Bank.ToString()));
            parameters.Add(new("startAddress", field.Address.ToString()));
            parameters.Add(new("dataLength", field.Length.ToString()));
        }

        var reply = await ExecuteCommandAsync("readTagMemory", parameters, DefaultTimeout, cancellationToken);
        return reply.TagData ?? Array.Empty<RfTag>();
    }

    /// <summary>
    /// Write data to tag memory at specific memory banks by bank number, address, and data
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="tagId">Tag ID to write (empty to write any tag in field)</param>
    /// <param name="password">Access password for the tag (empty if not required)</param>
    /// <param name="tagFields">Array of memory fields specifying bank, address, length, and data to write</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array of tags that were written</returns>
    public async Task<RfTag[]> WriteTagMemoryAsync(string sourceName, string tagId, string password, RfTagField[] tagFields, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("sourceName", sourceName)
        };

        if (!string.IsNullOrEmpty(tagId))
            parameters.Add(new("tagID", tagId));

        if (!string.IsNullOrEmpty(password))
            parameters.Add(new("password", password));

        foreach (var field in tagFields)
        {
            parameters.Add(new("tagField"));
            
            // Convert bank number to string for XML
            parameters.Add(new("bank", field.Bank.ToString()));
            parameters.Add(new("startAddress", field.Address.ToString()));
            parameters.Add(new("dataLength", field.Length.ToString()));
            parameters.Add(new("data", field.Data));
        }

        var reply = await ExecuteCommandAsync("writeTagMemory", parameters, DefaultTimeout, cancellationToken);
        return reply.TagData ?? Array.Empty<RfTag>();
    }

    /// <summary>
    /// Read tag field by predefined field name (requires reader configuration with field definitions)
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="tagId">Tag ID to read (empty to read any tag in field)</param>
    /// <param name="password">Access password for the tag (empty if not required)</param>
    /// <param name="fieldNames">Array of field names to read (must be defined in reader configuration)</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array of tags with field data populated</returns>
    public async Task<RfTag[]> ReadTagFieldAsync(string sourceName, string tagId, string password, string[] fieldNames, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("sourceName", sourceName)
        };

        if (!string.IsNullOrEmpty(tagId))
            parameters.Add(new("tagID", tagId));

        if (!string.IsNullOrEmpty(password))
            parameters.Add(new("password", password));

        foreach (var fieldName in fieldNames)
        {
            parameters.Add(new("tagField"));
            parameters.Add(new("fieldName"));
        }

        var reply = await ExecuteCommandAsync("readTagField", parameters, DefaultTimeout, cancellationToken);
        return reply.TagData ?? Array.Empty<RfTag>();
    }

    /// <summary>
    /// Write tag field by predefined field name (requires reader configuration with field definitions)
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="tagId">Tag ID to write (empty to write any tag in field)</param>
    /// <param name="password">Access password for the tag (empty if not required)</param>
    /// <param name="fieldData">Dictionary of field names and data values to write</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array of tags that were written</returns>
    public async Task<RfTag[]> WriteTagFieldAsync(string sourceName, string tagId, string password, Dictionary<string, string> fieldData, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("sourceName", sourceName)
        };

        if (!string.IsNullOrEmpty(tagId))
            parameters.Add(new("tagID", tagId));

        if (!string.IsNullOrEmpty(password))
            parameters.Add(new("password", password));

        foreach (var (fieldName, data) in fieldData)
        {
            parameters.Add(new("tagField"));
            parameters.Add(new("fieldName", fieldName));
            parameters.Add(new("data", data));
        }

        var reply = await ExecuteCommandAsync("writeTagField", parameters, DefaultTimeout, cancellationToken);
        return reply.TagData ?? Array.Empty<RfTag>();
    }

    #endregion

    #region Tag Security Operations

    /// <summary>
    /// Kill a tag permanently (irreversible operation - tag will become non-responsive)
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="tagId">Tag ID to kill</param>
    /// <param name="killPassword">32-bit kill password in hexadecimal format</param>
    /// <param name="recommissioningFlags">Optional recommissioning flags for special tag types</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array containing the killed tag information</returns>
    public async Task<RfTag[]> KillTagAsync(string sourceName, string tagId, string killPassword, string? recommissioningFlags = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>();

        if (!string.IsNullOrEmpty(sourceName))
            parameters.Add(new("sourceName", sourceName));

        if (!string.IsNullOrEmpty(tagId))
            parameters.Add(new("tagID", tagId));

        parameters.Add(new("password", killPassword));

        if (!string.IsNullOrEmpty(recommissioningFlags))
            parameters.Add(new("recomFlags", recommissioningFlags));

        var reply = await ExecuteCommandAsync("killTag", parameters, DefaultTimeout, cancellationToken);
        return reply.TagData ?? Array.Empty<RfTag>();
    }

    /// <summary>
    /// Lock or unlock tag memory banks to prevent or allow modifications
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="tagId">Tag ID to lock</param>
    /// <param name="action">Lock action bitmap (20-bit value defining lock state for each memory bank)</param>
    /// <param name="mask">Lock mask bitmap (20-bit value defining which banks to modify)</param>
    /// <param name="password">Access password for the tag</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array containing the locked tag information</returns>
    public async Task<RfTag[]> LockTagBankAsync(string sourceName, string tagId, uint action, uint mask, string password, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>();

        if (!string.IsNullOrEmpty(sourceName))
            parameters.Add(new("sourceName", sourceName));

        if (!string.IsNullOrEmpty(tagId))
            parameters.Add(new("tagID", tagId));

        parameters.Add(new("lockAction", Convert.ToString(action, 2)));
        parameters.Add(new("lockMask", Convert.ToString(mask, 2)));
        parameters.Add(new("password", password));

        var reply = await ExecuteCommandAsync("lockTagBank", parameters, DefaultTimeout, cancellationToken);
        return reply.TagData ?? Array.Empty<RfTag>();
    }

    #endregion

    #region Configuration Operations

    /// <summary>
    /// Set complete reader configuration from XML content
    /// </summary>
    /// <param name="configXmlContent">XML configuration content (will be wrapped in CDATA section)</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Configuration ID after successful configuration</returns>
    public async Task<RfConfigId> SetConfigurationAsync(string configXmlContent, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("configData", $"<![CDATA[{configXmlContent}]]>")
        };

        var reply = await ExecuteCommandAsync("setConfiguration", parameters, LongTimeout, cancellationToken);
        var configId = GetParameter(reply.Parameters, "configID");

        return new RfConfigId { ConfigID = configId };
    }

    /// <summary>
    /// Get current reader configuration as XML
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Configuration ID and complete XML configuration</returns>
    public async Task<RfConfigId> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getConfiguration", null, LongTimeout, cancellationToken);

        var configId = GetParameter(reply.Parameters, "configID");
        var configData = GetParameter(reply.Parameters, "configData");

        return new RfConfigId { ConfigID = configId, Configuration = configData };
    }

    /// <summary>
    /// Get configuration version and type information
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Configuration ID and type</returns>
    public async Task<RfConfigId> GetConfigVersionAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getConfigVersion", null, DefaultTimeout, cancellationToken);

        var configId = GetParameter(reply.Parameters, "configID");
        var configType = GetParameter(reply.Parameters, "configType");

        return new RfConfigId { ConfigID = configId, ConfigType = configType };
    }

    /// <summary>
    /// Get currently active reader configuration as XML
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Configuration ID and active XML configuration</returns>
    public async Task<RfConfigId> GetActiveConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getActiveConfiguration", null, LongTimeout, cancellationToken);

        var configId = GetParameter(reply.Parameters, "configID");
        var configData = GetParameter(reply.Parameters, "configData");

        return new RfConfigId { ConfigID = configId, Configuration = configData };
    }

    #endregion

    #region Parameter Operations

    /// <summary>
    /// Set a single configuration parameter on the reader
    /// </summary>
    /// <param name="name">Parameter name</param>
    /// <param name="value">Parameter value</param>
    /// <param name="objType">Optional object type (e.g., "antenna", "source")</param>
    /// <param name="objName">Optional object name (e.g., "Antenna1")</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task SetParameterAsync(string name, string value, string? objType = null, string? objName = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("name", name),
            new("value", value)
        };

        if (!string.IsNullOrEmpty(objType))
            parameters.Add(new("objType", objType));

        if (!string.IsNullOrEmpty(objName))
            parameters.Add(new("objName", objName));

        await ExecuteCommandAsync("setParameter", parameters, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Get a single configuration parameter from the reader
    /// </summary>
    /// <param name="name">Parameter name</param>
    /// <param name="objType">Optional object type (e.g., "antenna", "source")</param>
    /// <param name="objName">Optional object name (e.g., "Antenna1")</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Parameter value as string</returns>
    public async Task<string> GetParameterAsync(string name, string? objType = null, string? objName = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("name", name) };

        if (!string.IsNullOrEmpty(objType))
            parameters.Add(new("objType", objType));

        if (!string.IsNullOrEmpty(objName))
            parameters.Add(new("objName", objName));

        var reply = await ExecuteCommandAsync("getParameter", parameters, DefaultTimeout, cancellationToken);
        return reply.Parameters.FirstOrDefault()?.Value ?? string.Empty;
    }

    #endregion

    #region Antenna Configuration

    /// <summary>
    /// Set antenna configuration parameters
    /// </summary>
    /// <param name="antennaName">Name of the antenna to configure</param>
    /// <param name="parameters">Dictionary of parameter names and values (e.g., transmitPower, receiveSensitivity)</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task SetAntennaConfigAsync(string antennaName, Dictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        var xmlParams = new List<XmlParameter>
        {
            new("antenna"),
            new("antennaName", antennaName)
        };

        foreach (var (key, value) in parameters)
        {
            xmlParams.Add(new(key, value));
        }
        xmlParams[^1] = xmlParams[^1] with { IsLastChild = true };

        await ExecuteCommandAsync("setAntennaConfig", xmlParams, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Get configuration for all antennas
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Dictionary mapping antenna names to their configuration parameters</returns>
    public async Task<Dictionary<string, Dictionary<string, string>>> GetAntennaConfigAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getAntennaConfig", null, DefaultTimeout, cancellationToken);

        var antennas = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? currentAntenna = null;
        string? currentAntennaName = null;

        foreach (var param in reply.Parameters)
        {
            if (param.IsContainer && param.Key == "antenna")
            {
                if (currentAntenna != null && currentAntennaName != null)
                {
                    antennas[currentAntennaName] = currentAntenna;
                }
                currentAntenna = new Dictionary<string, string>();
                currentAntennaName = null;
            }
            else if (currentAntenna != null)
            {
                if (param.Key == "antennaName")
                    currentAntennaName = param.Value;
                else
                    currentAntenna[param.Key] = param.Value;
            }
        }

        if (currentAntenna != null && currentAntennaName != null)
        {
            antennas[currentAntennaName] = currentAntenna;
        }

        return antennas;
    }

    #endregion

    #region Protocol Configuration

    /// <summary>
    /// Set RFID protocol configuration (EPC Gen2 parameters)
    /// </summary>
    /// <param name="initialQ">Initial Q value for tag population estimation (0-15)</param>
    /// <param name="profile">Link profile: 0=dense reader mode, 1=normal, 2=dense tag environment</param>
    /// <param name="channels">Frequency channels to use (e.g., "1-4" for channels 1 through 4)</param>
    /// <param name="retry">Number of retries for failed operations</param>
    /// <param name="idLength">Expected EPC ID length in words (0=variable)</param>
    /// <param name="writeBoost">Write power boost: 0=off, 1=on</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task SetProtocolConfigAsync(uint initialQ, uint profile, string channels, uint retry, uint idLength, uint writeBoost, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("initialQ", initialQ.ToString()),
            new("profile", profile.ToString()),
            new("channels", channels),
            new("retry", retry.ToString()),
            new("idLength", idLength.ToString()),
            new("writeBoost", writeBoost.ToString())
        };

        await ExecuteCommandAsync("setProtocolConfig", parameters, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Get current RFID protocol configuration
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Dictionary of protocol configuration parameters and values</returns>
    public async Task<Dictionary<string, string>> GetProtocolConfigAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getProtocolConfig", null, DefaultTimeout, cancellationToken);
        return reply.Parameters.ToDictionary(p => p.Key, p => p.Value);
    }

    #endregion

    #region Network Configuration

    /// <summary>
    /// Set network IP configuration (requires reader restart to take effect)
    /// </summary>
    /// <param name="ipAddress">Static IP address (e.g., "192.168.1.100")</param>
    /// <param name="subnetMask">Subnet mask (e.g., "255.255.255.0")</param>
    /// <param name="gateway">Default gateway (e.g., "192.168.1.1")</param>
    /// <param name="dhcpEnabled">Enable DHCP (if true, static IP settings are ignored)</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task SetIpConfigAsync(string? ipAddress = null, string? subnetMask = null, string? gateway = null, bool? dhcpEnabled = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>();

        if (dhcpEnabled.HasValue)
        {
            parameters.Add(new("dHCPEnable", dhcpEnabled.Value.ToString().ToLower()));
        }
        else
        {
            if (!string.IsNullOrEmpty(ipAddress))
                parameters.Add(new("iPAddress", ipAddress));

            if (!string.IsNullOrEmpty(subnetMask))
                parameters.Add(new("subNetMask", subnetMask));

            if (!string.IsNullOrEmpty(gateway))
                parameters.Add(new("gateway", gateway));
        }

        await ExecuteCommandAsync("setIPConfig", parameters, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Get current network IP configuration
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>IP configuration including address, subnet mask, gateway, and DHCP status</returns>
    public async Task<RfIpConfig> GetIpConfigAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getIPConfig", null, DefaultTimeout, cancellationToken);

        var config = new RfIpConfig();
        foreach (var param in reply.Parameters)
        {
            switch (param.Key)
            {
                case "iPAddress":
                    config.IpAddress = param.Value;
                    break;
                case "subNetMask":
                    config.SubnetMask = param.Value;
                    break;
                case "gateway":
                    config.Gateway = param.Value;
                    break;
                case "dHCPEnable":
                    config.DhcpEnabled = bool.Parse(param.Value);
                    break;
            }
        }

        return config;
    }

    #endregion

    #region Status Operations

    /// <summary>
    /// Get current reader operational status
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Dictionary of status parameters and values</returns>
    public async Task<Dictionary<string, string>> GetReaderStatusAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getReaderStatus", null, DefaultTimeout, cancellationToken);
        return reply.Parameters.ToDictionary(p => p.Key, p => p.Value);
    }

    /// <summary>
    /// Get device information and firmware version
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Dictionary of device information including firmware version, hardware revision, etc.</returns>
    public async Task<Dictionary<string, string>> GetDeviceStatusAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getDeviceStatus", null, DefaultTimeout, cancellationToken);

        var status = new Dictionary<string, string>();
        foreach (var param in reply.Parameters)
        {
            if (param.Key == "version")
            {
                var parts = param.Value.Split('=');
                if (parts.Length == 2)
                    status[parts[0]] = parts[1];
            }
            else
            {
                status[param.Key] = param.Value;
            }
        }
        return status;
    }

    /// <summary>
    /// Get detailed status information about tags in the field
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="mode">Status mode (e.g., "All", "Visible")</param>
    /// <param name="tagId">Optional specific tag ID to query</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Dictionary of tag status parameters</returns>
    public async Task<Dictionary<string, string>> GetTagStatusAsync(string sourceName, string? mode = null, string? tagId = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("sourceName", sourceName) };

        if (!string.IsNullOrEmpty(mode))
            parameters.Add(new("mode", mode));

        if (!string.IsNullOrEmpty(tagId))
            parameters.Add(new("tagID", tagId));

        var reply = await ExecuteCommandAsync("getTagStatus", parameters, DefaultTimeout, cancellationToken);

        var status = new Dictionary<string, string>();
        foreach (var param in reply.Parameters)
        {
            if (param.Key == "value")
            {
                var parts = param.Value.Split('=');
                if (parts.Length == 2)
                    status[parts[0]] = parts[1];
            }
            else
            {
                status[param.Key] = param.Value;
            }
        }
        return status;
    }

    #endregion

    #region I/O Operations

    /// <summary>
    /// Set digital output ports state
    /// </summary>
    /// <param name="outPortValue">Output value as binary string (e.g., "1010" for 4 ports)</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task SetIoAsync(string outPortValue, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("outValue", outPortValue) };
        await ExecuteCommandAsync("setIO", parameters, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Get current state of digital input and output ports
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>I/O port status with input and output values</returns>
    public async Task<RfIoPort> GetIoAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getIO", null, DefaultTimeout, cancellationToken);

        var ioPort = new RfIoPort();
        foreach (var param in reply.Parameters)
        {
            if (param.Key == "inValue")
                ioPort.InValue = param.Value;
            else if (param.Key == "outValue")
                ioPort.OutValue = param.Value;
        }

        return ioPort;
    }

    #endregion

    #region Time Operations

    /// <summary>
    /// Set reader's system time
    /// </summary>
    /// <param name="utcTime">UTC time to set on the reader</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task SetTimeAsync(DateTime utcTime, CancellationToken cancellationToken = default)
    {
        var timeStr = utcTime.ToString("yyyy-MM-ddTHH:mm:ss.fff+00:00");
        var parameters = new List<XmlParameter> { new("utcTime", timeStr) };
        await ExecuteCommandAsync("setTime", parameters, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Get reader's current system time
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Current UTC time from the reader</returns>
    public async Task<DateTime> GetTimeAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getTime", null, DefaultTimeout, cancellationToken);
        var utcTimeStr = GetParameter(reply.Parameters, "utcTime");
        return DateTime.ParseExact(utcTimeStr, "yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
    }

    #endregion

    #region Blacklist Operations

    /// <summary>
    /// Edit the blacklist (tags to ignore during reads)
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="command">Blacklist command: "ADD" to add tags, "DEL" to remove tags, "CLEAR" to clear all</param>
    /// <param name="tagIds">Array of tag IDs to add or remove (not needed for "CLEAR" command)</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task EditBlacklistAsync(string sourceName, string command, string[]? tagIds = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter>
        {
            new("sourceName", sourceName),
            new("blackListCmd", command)
        };

        if (tagIds != null)
        {
            foreach (var tagId in tagIds)
            {
                parameters.Add(new("tagID", tagId));
            }
        }

        await ExecuteCommandAsync("editBlacklist", parameters, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Get all tags currently in the blacklist
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array of blacklisted tag IDs</returns>
    public async Task<RfTag[]> GetBlacklistAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("sourceName", sourceName) };
        var reply = await ExecuteCommandAsync("getBlacklist", parameters, DefaultTimeout, cancellationToken);
        return reply.TagData ?? Array.Empty<RfTag>();
    }

    #endregion

    #region Source Operations

    /// <summary>
    /// Get list of all available antenna sources configured on the reader
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>Array of source names (antennas and antenna groups)</returns>
    public async Task<string[]> GetAllSourcesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getAllSources", null, DefaultTimeout, cancellationToken);
        return reply.Parameters.Where(p => p.Key == "sourceName").Select(p => p.Value).ToArray();
    }

    /// <summary>
    /// Stop currently executing command on a source
    /// </summary>
    /// <param name="sourceName">Name of the antenna source</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task StopCommandAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("sourceName", sourceName) };
        await ExecuteCommandAsync("stopCommand", parameters, DefaultTimeout, cancellationToken);
    }

    #endregion

    #region System Operations

    /// <summary>
    /// Reset the reader
    /// </summary>
    /// <param name="resetType">Reset type: "SW" for software reset, "HW" for hardware reset</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    public async Task ResetReaderAsync(string resetType = "SW", CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("resetType", resetType) };
        await ExecuteCommandAsync("resetReader", parameters, LongTimeout, cancellationToken);
    }

    #endregion

    #region Core Command Execution

    private async Task<CommandReply> ExecuteCommandAsync(string commandName, List<XmlParameter>? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_isConnected || _stream == null)
            throw new InvalidOperationException("Not connected to reader");

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            var commandId = Interlocked.Increment(ref _commandId).ToString();
            var xml = BuildXmlCommand(commandName, commandId, parameters);

            // Send command
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(xml);
                await _stream.WriteAsync(bytes, cancellationToken);
                await _stream.FlushAsync(cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }

            // Wait for reply
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    lock (_pendingReplies)
                    {
                        var reply = _pendingReplies.FirstOrDefault(r => r.CommandID == commandId);
                        if (reply != null)
                        {
                            _pendingReplies.Remove(reply);

                            if (reply.ResultCode != 0)
                                throw new RfReaderException(reply.ResultCode, reply.Error, reply.Cause);

                            return reply;
                        }
                    }

                    await Task.Delay(10, cts.Token);
                }

                throw new TimeoutException($"Command '{commandName}' timed out after {timeout.TotalSeconds}s");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Command '{commandName}' timed out after {timeout.TotalSeconds}s");
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    #region Diagnostic Methods

    /// <summary>
    /// Get the last sent XML command (for debugging purposes)
    /// </summary>
    public string LastSentCommand { get; private set; } = string.Empty;

    /// <summary>
    /// Get the last received XML response (for debugging purposes)
    /// </summary>
    public string LastReceivedResponse { get; private set; } = string.Empty;

    /// <summary>
    /// Enable or disable diagnostic logging to console
    /// </summary>
    public bool EnableDiagnostics { get; set; } = false;

    private string BuildXmlCommand(string commandName, string commandId, List<XmlParameter>? parameters)
    {
        var sb = new StringBuilder();
        sb.Append("<frame><cmd><id>").Append(commandId).Append("</id>");
        sb.Append('<').Append(commandName).Append('>');

        if (parameters != null)
        {
            AddParameters(sb, parameters.GetEnumerator());
        }

        sb.Append("</").Append(commandName).Append(">");
        sb.Append("</cmd></frame>");

        var xml = sb.ToString();
        
        if (EnableDiagnostics)
        {
            LastSentCommand = xml;
            Console.WriteLine($"[SEND] {xml}");
        }

        return xml;
    }

    #endregion
    
    private void AddParameters(StringBuilder sb, IEnumerator<XmlParameter> paramEnum)
    {
        while (paramEnum.MoveNext())
        {
            var param = paramEnum.Current;

            if (param.IsContainer)
            {
                // Open container tag
                sb.Append('<').Append(param.Key).Append('>');

                // If container is not empty, recursively add child elements
                if (!param.IsEmpty)
                {
                    AddParameters(sb, paramEnum);
                }

                // Close container tag
                sb.Append("</").Append(param.Key).Append('>');
                
                // If this container is marked as last child, stop processing
                if (param.IsLastChild)
                    break;
            }
            else
            {
                // Regular parameter - add as simple element
                if (string.IsNullOrEmpty(param.Value))
                {
                    // Empty element like <heartBeat/>
                    sb.Append('<').Append(param.Key).Append("/>");
                }
                else
                {
                    // Element with value
                    sb.Append('<').Append(param.Key).Append('>');
                    sb.Append(System.Security.SecurityElement.Escape(param.Value)); // XML escape special chars
                    sb.Append("</").Append(param.Key).Append('>');
                }

                // If this is the last child, stop processing
                if (param.IsLastChild)
                    break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        var messageBuilder = new StringBuilder();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream != null)
            {
                var bytesRead = await _stream.ReadAsync(buffer, cancellationToken);

                if (bytesRead == 0)
                    break;

                var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                messageBuilder.Append(data);

                // Extract complete XML frames
                var xml = messageBuilder.ToString();
                var startIdx = 0;

                while (true)
                {
                    var frameStart = xml.IndexOf("<frame>", startIdx, StringComparison.Ordinal);
                    if (frameStart == -1)
                        break;

                    var frameEnd = xml.IndexOf("</frame>", frameStart, StringComparison.Ordinal);
                    if (frameEnd == -1)
                        break;

                    frameEnd += "</frame>".Length;
                    var frame = xml.Substring(frameStart, frameEnd - frameStart);

                    ProcessReceivedFrame(frame);

                    startIdx = frameEnd;
                }

                // Keep remaining partial data
                if (startIdx > 0)
                    messageBuilder.Remove(0, startIdx);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelling
        }
        catch (Exception)
        {
            _isConnected = false;
        }
    }

    private void ProcessReceivedFrame(string xml)
    {
        try
        {
            if (EnableDiagnostics)
            {
                LastReceivedResponse = xml;
                Console.WriteLine($"[RECV] {xml}");
            }

            var reply = ParseXmlReply(xml);

            if (EnableDiagnostics)
            {
                Console.WriteLine($"[PARSE] Command ID: {reply.CommandID}, Result: {reply.ResultCode}, Tags: {reply.TagData?.Length ?? 0}");
                if (reply.TagData != null)
                {
                    foreach (var tag in reply.TagData)
                    {
                        Console.WriteLine($"[TAG] ID: {tag.TagId}, Fields: {tag.Fields.Count}");
                    }
                }
            }

            lock (_pendingReplies)
            {
                _pendingReplies.Add(reply);
            }
        }
        catch (Exception ex)
        {
            if (EnableDiagnostics)
            {
                Console.WriteLine($"[ERROR] Failed to parse: {ex.Message}");
                Console.WriteLine($"[XML] {xml}");
            }
        }
    }

    private CommandReply ParseXmlReply(string xml)
    {
        var reply = new CommandReply();
        var parameters = new List<XmlParameter>();
        var tags = new List<RfTag>();

        using var reader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(reader, new XmlReaderSettings 
        { 
            IgnoreWhitespace = true,
            IgnoreComments = true
        });

        while (xmlReader.Read())
        {
            if (xmlReader.NodeType != XmlNodeType.Element)
                continue;

            switch (xmlReader.Name)
            {
                case "id":
                    reply.CommandID = ReadElementText(xmlReader);
                    break;
                    
                case "resultCode":
                    var resultCodeText = ReadElementText(xmlReader);
                    if (int.TryParse(resultCodeText, out var resultCode))
                    {
                        reply.ResultCode = resultCode;
                    }
                    break;
                    
                case "error":
                    reply.Error = ReadElementText(xmlReader);
                    break;
                    
                case "cause":
                    reply.Cause = ReadElementText(xmlReader);
                    break;
                    
                case "tag":
                    tags.Add(ParseTagElement(xmlReader));
                    break;
                    
                // These are the key parameters that can be at different nesting levels
                case "version":
                    var version = ReadElementText(xmlReader);
                    if (!string.IsNullOrEmpty(version))
                    {
                        parameters.Add(new XmlParameter("version", version));
                    }
                    break;
                    
                case "configID":
                    var configId = ReadElementText(xmlReader);
                    if (!string.IsNullOrEmpty(configId))
                    {
                        parameters.Add(new XmlParameter("configID", configId));
                    }
                    break;
                    
                case "configType":
                    var configType = ReadElementText(xmlReader);
                    if (!string.IsNullOrEmpty(configType))
                    {
                        parameters.Add(new XmlParameter("configType", configType));
                    }
                    break;
                    
                case "configData":
                    var configData = ReadElementText(xmlReader);
                    if (!string.IsNullOrEmpty(configData))
                    {
                        parameters.Add(new XmlParameter("configData", configData));
                    }
                    break;
                    
                default:
                    // Capture other parameters (but skip known containers)
                    if (!IsKnownContainerElement(xmlReader.Name))
                    {
                        var value = ReadElementText(xmlReader);
                        if (!string.IsNullOrEmpty(value))
                        {
                            parameters.Add(new XmlParameter(xmlReader.Name, value));
                        }
                    }
                    break;
            }
        }

        reply.Parameters = parameters;
        reply.TagData = tags.Count > 0 ? tags.ToArray() : null;

        return reply;
    }

    /// <summary>
    /// Parse a single tag element
    /// </summary>
    private RfTag ParseTagElement(XmlReader reader)
    {
        var tag = new RfTag();
        
        if (reader.IsEmptyElement)
            return tag;

        var tagDepth = reader.Depth;
        var currentTagField = new Dictionary<string, string>();
        var tagFieldIndex = 0;

        while (reader.Read())
        {
            // Exit when we reach the closing </tag>
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "tag" && reader.Depth == tagDepth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "tagID":
                        tag.TagId = ReadElementText(reader);
                        break;
                        
                    case "data":
                        // Check if we're inside a tagField
                        if (currentTagField.Count > 0)
                        {
                            currentTagField["data"] = ReadElementText(reader);
                        }
                        else
                        {
                            tag.Data = ReadElementText(reader);
                        }
                        break;
                        
                    case "tagPC":
                        tag.Fields["tagPC"] = ReadElementText(reader);
                        break;
                        
                    case "success":
                        tag.Fields["success"] = ReadElementText(reader);
                        break;
                        
                    case "utcTime":
                        tag.Fields["utcTime"] = ReadElementText(reader);
                        break;
                        
                    case "antennaName":
                        tag.Fields["antennaName"] = ReadElementText(reader);
                        break;
                        
                    case "rSSI":
                    case "rssi":
                        tag.Fields["rssi"] = ReadElementText(reader);
                        break;
                        
                    case "antenna":
                        tag.Fields["antenna"] = ReadElementText(reader);
                        break;
                        
                    case "timestamp":
                        tag.Fields["timestamp"] = ReadElementText(reader);
                        break;
                        
                    case "readCount":
                        tag.Fields["readCount"] = ReadElementText(reader);
                        break;
                        
                    case "tagField":
                        // Start of a new tag field container
                        currentTagField = new Dictionary<string, string>();
                        break;
                        
                    case "bank":
                        if (currentTagField.Count >= 0)
                        {
                            currentTagField["bank"] = ReadElementText(reader);
                        }
                        break;
                        
                    case "startAddress":
                        if (currentTagField.Count >= 0)
                        {
                            currentTagField["startAddress"] = ReadElementText(reader);
                        }
                        break;
                        
                    case "dataLength":
                        if (currentTagField.Count >= 0)
                        {
                            currentTagField["dataLength"] = ReadElementText(reader);
                        }
                        break;
                        
                    case "fieldName":
                        tag.Fields["fieldName"] = ReadElementText(reader);
                        break;
                        
                    default:
                        // Capture any other fields
                        if (!IsKnownContainerElement(reader.Name))
                        {
                            var value = ReadElementText(reader);
                            if (!string.IsNullOrEmpty(value))
                            {
                                tag.Fields[reader.Name] = value;
                            }
                        }
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "tagField")
            {
                // End of tagField - store the collected data
                if (currentTagField.Count > 0)
                {
                    var bank = currentTagField.ContainsKey("bank") ? currentTagField["bank"] : "unknown";
                    var data = currentTagField.ContainsKey("data") ? currentTagField["data"] : "";
                    var startAddr = currentTagField.ContainsKey("startAddress") ? currentTagField["startAddress"] : "0";
                    var dataLen = currentTagField.ContainsKey("dataLength") ? currentTagField["dataLength"] : "0";
                    
                    // Store with descriptive key
                    var bankName = bank switch
                    {
                        "0" => "RESERVED",
                        "1" => "EPC",
                        "2" => "TID",
                        "3" => "USER",
                        _ => $"Bank{bank}"
                    };
                    
                    tag.Fields[$"{bankName}_Data"] = data;
                    tag.Fields[$"{bankName}_Bank"] = bank;
                    tag.Fields[$"{bankName}_StartAddress"] = startAddr;
                    tag.Fields[$"{bankName}_DataLength"] = dataLen;
                    
                    currentTagField.Clear();
                    tagFieldIndex++;
                }
            }
        }

        return tag;
    }

    /// <summary>
    /// Safely read element text, handling both simple and complex elements
    /// </summary>
    private string ReadElementText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return string.Empty;

        var elementName = reader.Name;
        var depth = reader.Depth;
        var text = new StringBuilder();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
            {
                text.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == elementName && reader.Depth == depth)
            {
                break;
            }
        }

        return text.ToString();
    }

    private bool IsKnownContainerElement(string name)
    {
        return name switch
        {
            "frame" or "cmd" or "reply" or "antenna" or "tagField" or "param" 
            or "supportedVersions" or "blacklist" or "tagData" or "tags" 
            or "returnValue" or "readTagIDs" or "getObservedTagIDs" 
            or "writeTagMemory" or "readTagMemory" or "hostGreetings" 
            or "hostGoodbye" or "heartBeat" => true,
            _ => false
        };
    }

    private string GetParameter(List<XmlParameter> parameters, string key)
    {
        return parameters.FirstOrDefault(p => p.Key == key)?.Value ?? string.Empty;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the client and releases all resources synchronously
    /// </summary>
    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _commandLock.Dispose();
        _sendLock.Dispose();
        _receiveCts.Dispose();
    }

    /// <summary>
    /// Asynchronously disposes the client and releases all resources
    /// </summary>
    /// <returns>A task representing the asynchronous dispose operation</returns>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _commandLock.Dispose();
        _sendLock.Dispose();
        _receiveCts.Dispose();
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Represents an XML parameter for RFID reader commands
/// </summary>
/// <param name="Key">Parameter name or XML element name</param>
/// <param name="Value">Parameter value (empty for container elements)</param>
/// <param name="IsContainer">Indicates if this is a container element that holds child elements</param>
/// <param name="IsEmpty">Indicates if this container is empty (no children)</param>
/// <param name="IsLastChild">Indicates if this is the last child in a container (used for XML parsing)</param>
public record XmlParameter(string Key, string Value = "", bool IsContainer = false, bool IsEmpty = false, bool IsLastChild = false);

/// <summary>
/// Internal class representing a command reply from the RFID reader
/// </summary>
public class CommandReply
{
    /// <summary>
    /// Gets or sets the command identifier that this reply corresponds to
    /// </summary>
    public string CommandID { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the result code (0 = success, non-zero = error)
    /// </summary>
    public int ResultCode { get; set; }

    /// <summary>
    /// Gets or sets the error message if result code is non-zero
    /// </summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the error cause description
    /// </summary>
    public string Cause { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of parameters returned in the reply
    /// </summary>
    public List<XmlParameter> Parameters { get; set; } = new();

    /// <summary>
    /// Gets or sets the array of tag data if tags were returned
    /// </summary>
    public RfTag[]? TagData { get; set; }
}

/// <summary>
/// Represents an RFID tag with its ID, data, and associated metadata
/// </summary>
public class RfTag
{
    /// <summary>
    /// Gets or sets the tag's EPC (Electronic Product Code) identifier in hexadecimal format
    /// </summary>
    public string TagId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary data associated with the tag
    /// </summary>
    public string Data { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets additional fields and metadata (RSSI, antenna, timestamp, memory banks, etc.)
    /// </summary>
    public Dictionary<string, string> Fields { get; set; } = new();
}

/// <summary>
/// Represents a memory field in an RFID tag
/// </summary>
public class RfTagField
{
    /// <summary>
    /// Memory bank number (0-3)
    /// <para>EPC Gen2 Memory Banks:</para>
    /// <list type="bullet">
    ///   <item><description>0 = RESERVED - Contains kill and access passwords</description></item>
    ///   <item><description>1 = EPC - Electronic Product Code (main tag identifier)</description></item>
    ///   <item><description>2 = TID - Tag Identifier (manufacturer info, chip model)</description></item>
    ///   <item><description>3 = USER - User-defined data storage</description></item>
    /// </list>
    /// </summary>
    public uint Bank { get; set; }
    
    /// <summary>
    /// Start address within the memory bank (in words, 1 word = 16 bits)
    /// </summary>
    public uint Address { get; set; }
    
    /// <summary>
    /// Number of words to read/write (1 word = 16 bits = 2 bytes)
    /// </summary>
    public uint Length { get; set; }
    
    /// <summary>
    /// Data to write (hexadecimal string) or data read from the tag
    /// </summary>
    public string Data { get; set; } = string.Empty;
}

/// <summary>
/// Represents reader configuration identification and data
/// </summary>
public class RfConfigId
{
    /// <summary>
    /// Gets or sets the protocol version
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the configuration identifier (hash/checksum of configuration)
    /// </summary>
    public string ConfigID { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the configuration type (e.g., "XML", "Binary")
    /// </summary>
    public string ConfigType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the complete configuration data as XML string
    /// </summary>
    public string Configuration { get; set; } = string.Empty;
}

/// <summary>
/// Represents network IP configuration for the RFID reader
/// </summary>
public class RfIpConfig
{
    /// <summary>
    /// Gets or sets the IP address
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subnet mask
    /// </summary>
    public string SubnetMask { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default gateway
    /// </summary>
    public string Gateway { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether DHCP is enabled
    /// </summary>
    public bool DhcpEnabled { get; set; }
}

/// <summary>
/// Represents digital I/O port states on the RFID reader
/// </summary>
public class RfIoPort
{
    /// <summary>
    /// Gets or sets the input port values as binary string (e.g., "1010" for 4 input ports)
    /// </summary>
    public string InValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the output port values as binary string (e.g., "1010" for 4 output ports)
    /// </summary>
    public string OutValue { get; set; } = string.Empty;
}

/// <summary>
/// Exception thrown when an RFID reader returns an error
/// </summary>
public class RfReaderException : Exception
{
    /// <summary>
    /// Gets the error result code from the reader
    /// </summary>
    public int ResultCode { get; }

    /// <summary>
    /// Gets the error cause description
    /// </summary>
    public string Cause { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RfReaderException"/> class
    /// </summary>
    /// <param name="resultCode">Error result code</param>
    /// <param name="error">Error message</param>
    /// <param name="cause">Error cause description</param>
    public RfReaderException(int resultCode, string error, string cause)
        : base($"RFID Error {resultCode}: {error} - {cause}")
    {
        ResultCode = resultCode;
        Cause = cause;
    }
}

/// <summary>
/// EPC Gen2 Memory Bank constants
/// </summary>
public static class EpcMemoryBank
{
    /// <summary>
    /// Reserved memory bank - Contains kill and access passwords
    /// </summary>
    public const uint Reserved = 0;
    
    /// <summary>
    /// EPC memory bank - Electronic Product Code (main tag identifier)
    /// </summary>
    public const uint Epc = 1;
    
    /// <summary>
    /// TID memory bank - Tag Identifier (manufacturer info, chip model)
    /// </summary>
    public const uint Tid = 2;
    
    /// <summary>
    /// User memory bank - User-defined data storage
    /// </summary>
    public const uint User = 3;
}

#endregion