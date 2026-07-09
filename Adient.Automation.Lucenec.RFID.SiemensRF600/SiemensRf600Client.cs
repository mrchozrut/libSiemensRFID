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

    public bool IsConnected => _isConnected;
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan LongTimeout { get; set; } = TimeSpan.FromSeconds(20);

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
    /// Disconnect from the RFID reader
    /// </summary>
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
    /// Send host greetings command (should be first command)
    /// </summary>
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
    /// Send host goodbye command (should be last command)
    /// </summary>
    public async Task HostGoodbyeAsync(string readerMode = "Default", CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("readerMode", readerMode) };
        await ExecuteCommandAsync("hostGoodbye", parameters, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Check if reader is present (heartbeat)
    /// </summary>
    public async Task HeartBeatAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteCommandAsync("heartBeat", null, DefaultTimeout, cancellationToken);
    }

    #endregion

    #region Reader Control

    /// <summary>
    /// Start the reader (leave stop mode)
    /// </summary>
    public async Task StartReaderAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteCommandAsync("startReader", new List<XmlParameter>(), DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Stop the reader (enter stop mode)
    /// </summary>
    public async Task StopReaderAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteCommandAsync("stopReader", new List<XmlParameter>(), DefaultTimeout, cancellationToken);
    }

    #endregion

    #region Tag Inventory Operations

    /// <summary>
    /// Read tag IDs from specified source
    /// </summary>
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
    /// Get observed tag IDs (with smoothing algorithm)
    /// </summary>
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
    /// Trigger source for one reading cycle
    /// </summary>
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
    /// Write a new tag ID
    /// </summary>
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
    /// Read tag memory by bank, address, and length
    /// </summary>
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
    /// Write tag memory by bank, address, and data
    /// </summary>
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
    /// Read tag field by name
    /// </summary>
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
    /// Write tag field by name
    /// </summary>
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
    /// Kill a tag (permanent operation)
    /// </summary>
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
    /// Lock tag bank
    /// </summary>
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
    /// Set configuration from file content
    /// </summary>
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
    /// Get current configuration
    /// </summary>
    public async Task<RfConfigId> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getConfiguration", null, LongTimeout, cancellationToken);

        var configId = GetParameter(reply.Parameters, "configID");
        var configData = GetParameter(reply.Parameters, "configData");

        return new RfConfigId { ConfigID = configId, Configuration = configData };
    }

    /// <summary>
    /// Get configuration version
    /// </summary>
    public async Task<RfConfigId> GetConfigVersionAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getConfigVersion", null, DefaultTimeout, cancellationToken);

        var configId = GetParameter(reply.Parameters, "configID");
        var configType = GetParameter(reply.Parameters, "configType");

        return new RfConfigId { ConfigID = configId, ConfigType = configType };
    }

    /// <summary>
    /// Get active configuration
    /// </summary>
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
    /// Set a single parameter
    /// </summary>
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
    /// Get a single parameter
    /// </summary>
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
    /// Set antenna configuration
    /// </summary>
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
    /// Get antenna configuration
    /// </summary>
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
    /// Set protocol configuration
    /// </summary>
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
    /// Get protocol configuration
    /// </summary>
    public async Task<Dictionary<string, string>> GetProtocolConfigAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getProtocolConfig", null, DefaultTimeout, cancellationToken);
        return reply.Parameters.ToDictionary(p => p.Key, p => p.Value);
    }

    #endregion

    #region Network Configuration

    /// <summary>
    /// Set IP configuration
    /// </summary>
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
    /// Get IP configuration
    /// </summary>
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
    /// Get reader status
    /// </summary>
    public async Task<Dictionary<string, string>> GetReaderStatusAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getReaderStatus", null, DefaultTimeout, cancellationToken);
        return reply.Parameters.ToDictionary(p => p.Key, p => p.Value);
    }

    /// <summary>
    /// Get device status
    /// </summary>
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
    /// Get tag status
    /// </summary>
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
    /// Set output ports
    /// </summary>
    public async Task SetIoAsync(string outPortValue, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("outValue", outPortValue) };
        await ExecuteCommandAsync("setIO", parameters, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Get I/O port status
    /// </summary>
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
    /// Set reader time
    /// </summary>
    public async Task SetTimeAsync(DateTime utcTime, CancellationToken cancellationToken = default)
    {
        var timeStr = utcTime.ToString("yyyy-MM-ddTHH:mm:ss.fff+00:00");
        var parameters = new List<XmlParameter> { new("utcTime", timeStr) };
        await ExecuteCommandAsync("setTime", parameters, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Get reader time
    /// </summary>
    public async Task<DateTime> GetTimeAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getTime", null, DefaultTimeout, cancellationToken);
        var utcTimeStr = GetParameter(reply.Parameters, "utcTime");
        return DateTime.ParseExact(utcTimeStr, "yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
    }

    #endregion

    #region Blacklist Operations

    /// <summary>
    /// Edit blacklist (ADD, DEL, CLEAR)
    /// </summary>
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
    /// Get blacklist
    /// </summary>
    public async Task<RfTag[]> GetBlacklistAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("sourceName", sourceName) };
        var reply = await ExecuteCommandAsync("getBlacklist", parameters, DefaultTimeout, cancellationToken);
        return reply.TagData ?? Array.Empty<RfTag>();
    }

    #endregion

    #region Source Operations

    /// <summary>
    /// Get all available sources
    /// </summary>
    public async Task<string[]> GetAllSourcesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteCommandAsync("getAllSources", null, DefaultTimeout, cancellationToken);
        return reply.Parameters.Where(p => p.Key == "sourceName").Select(p => p.Value).ToArray();
    }

    /// <summary>
    /// Stop current command execution
    /// </summary>
    public async Task StopCommandAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        var parameters = new List<XmlParameter> { new("sourceName", sourceName) };
        await ExecuteCommandAsync("stopCommand", parameters, DefaultTimeout, cancellationToken);
    }

    #endregion

    #region System Operations

    /// <summary>
    /// Reset reader
    /// </summary>
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
    /// Get the last sent XML command (for debugging)
    /// </summary>
    public string LastSentCommand { get; private set; } = string.Empty;

    /// <summary>
    /// Get the last received XML response (for debugging)
    /// </summary>
    public string LastReceivedResponse { get; private set; } = string.Empty;

    /// <summary>
    /// Enable/disable diagnostic logging
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

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _commandLock.Dispose();
        _sendLock.Dispose();
        _receiveCts.Dispose();
    }

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

public record XmlParameter(string Key, string Value = "", bool IsContainer = false, bool IsEmpty = false, bool IsLastChild = false);

public class CommandReply
{
    public string CommandID { get; set; } = string.Empty;
    public int ResultCode { get; set; }
    public string Error { get; set; } = string.Empty;
    public string Cause { get; set; } = string.Empty;
    public List<XmlParameter> Parameters { get; set; } = new();
    public RfTag[]? TagData { get; set; }
}

public class RfTag
{
    public string TagId { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
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

public class RfConfigId
{
    public string Version { get; set; } = string.Empty;
    public string ConfigID { get; set; } = string.Empty;
    public string ConfigType { get; set; } = string.Empty;
    public string Configuration { get; set; } = string.Empty;
}

public class RfIpConfig
{
    public string IpAddress { get; set; } = string.Empty;
    public string SubnetMask { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public bool DhcpEnabled { get; set; }
}

public class RfIoPort
{
    public string InValue { get; set; } = string.Empty;
    public string OutValue { get; set; } = string.Empty;
}

public class RfReaderException : Exception
{
    public int ResultCode { get; }
    public string Cause { get; }

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