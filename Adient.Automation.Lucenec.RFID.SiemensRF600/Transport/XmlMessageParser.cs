using System.Text;

namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;

/// <summary>
/// Parses incoming XML messages from the stream
/// </summary>
internal class XmlMessageParser
{
    private readonly StringBuilder _buffer = new();
    private readonly int _maxMessageSize;
    private int _currentPosition;
    private string _currentStartTag = string.Empty;
    private bool _searchingForStartTag = true;

    private static readonly string[] ValidStartTags = { "<frame>", "<reply>", "<notification>", "<alarm>" };

    public XmlMessageParser(int maxMessageSize = 10 * 1024 * 1024)
    {
        _maxMessageSize = maxMessageSize;
    }

    /// <summary>
    /// Append received data to the buffer
    /// </summary>
    public void AppendData(string data)
    {
        if (_buffer.Length + data.Length > _maxMessageSize)
        {
            // Buffer overflow protection - clear and start over
            _buffer.Clear();
            _currentPosition = 0;
            _searchingForStartTag = true;
            _currentStartTag = string.Empty;
        }

        _buffer.Append(data);
    }

    /// <summary>
    /// Try to extract a complete XML message from the buffer
    /// </summary>
    public bool TryExtractMessage(out string message, out XmlMessageType messageType)
    {
        message = string.Empty;
        messageType = XmlMessageType.Unknown;

        while (_buffer.Length > 0)
        {
            if (_searchingForStartTag)
            {
                if (!FindStartTag())
                    return false;
            }

            if (TryFindCompleteMessage(out message, out messageType))
            {
                _searchingForStartTag = true;
                return true;
            }

            // Adjust position to avoid re-scanning partial end tags
            _currentPosition = Math.Max(0, _buffer.Length - _currentStartTag.Length);
            return false;
        }

        return false;
    }

    private bool FindStartTag()
    {
        _currentPosition = 0;
        _currentStartTag = string.Empty;
        const string xmlStartChar = "<";

        while (true)
        {
            var pos = _buffer.ToString().IndexOf(xmlStartChar, _currentPosition, StringComparison.Ordinal);

            if (pos == -1)
            {
                // No '<' found, clear entire buffer
                _buffer.Clear();
                return false;
            }

            // Remove everything before the '<'
            if (pos > 0)
            {
                _buffer.Remove(0, pos);
            }

            // Check if we have a valid start tag
            foreach (var startTag in ValidStartTags)
            {
                if (_buffer.Length < startTag.Length)
                    return false; // Not enough data yet

                if (_buffer.ToString(0, startTag.Length) == startTag)
                {
                    _currentStartTag = startTag;
                    _currentPosition = startTag.Length;
                    _searchingForStartTag = false;
                    return true;
                }
            }

            // Unknown start tag, skip the '<' and continue
            _buffer.Remove(0, 1);
        }
    }

    private bool TryFindCompleteMessage(out string message, out XmlMessageType messageType)
    {
        message = string.Empty;
        messageType = XmlMessageType.Unknown;

        if (_currentPosition <= 0)
            return false;

        // Build end tag from start tag (e.g., "<frame>" -> "frame>")
        var endTagSearch = _currentStartTag.Substring(1);
        var remainingText = _buffer.ToString(_currentPosition, _buffer.Length - _currentPosition);
        var endTagPos = remainingText.IndexOf(endTagSearch, StringComparison.Ordinal);

        if (endTagPos == -1)
            return false; // End tag not found

        // Check if it's actually an end tag (has </ before it) or a new start tag
        if (endTagPos > 0)
        {
            var checkPos = _currentPosition + endTagPos - 1;
            if (checkPos >= 0 && checkPos < _buffer.Length)
            {
                var surroundingText = _buffer.ToString(Math.Max(0, checkPos - 1), Math.Min(endTagSearch.Length + 2, _buffer.Length - checkPos + 1));

                // Check for new start tag
                if (surroundingText.Contains(_currentStartTag))
                {
                    // New message starting, extract everything before it
                    message = _buffer.ToString(0, checkPos);
                    _buffer.Remove(0, checkPos);
                    messageType = DetermineMessageType(message);
                    return true;
                }

                // Check for proper end tag
                if (surroundingText.Contains($"</{endTagSearch}"))
                {
                    var messageLength = _currentPosition + endTagPos + endTagSearch.Length;
                    message = _buffer.ToString(0, messageLength);
                    _buffer.Remove(0, messageLength);
                    messageType = DetermineMessageType(message);
                    return true;
                }
            }
        }

        return false;
    }

    private static XmlMessageType DetermineMessageType(string message)
    {
        if (message.Contains("<reply>"))
            return XmlMessageType.Reply;
        if (message.Contains("<report>"))
            return XmlMessageType.Report;
        if (message.Contains("<alarm>"))
            return XmlMessageType.Alarm;
        if (message.Contains("<notification>"))
            return XmlMessageType.Notification;

        return XmlMessageType.Unknown;
    }
}

internal enum XmlMessageType
{
    Unknown,
    Reply,
    Report,
    Alarm,
    Notification
}