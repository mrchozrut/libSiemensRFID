namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Models;

/// <summary>
/// Structured data for a single RFID tag with detection and memory information
/// </summary>
public class TagInfo
{
    /// <summary>
    /// Sequential index of the tag in the detection session
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// EPC (Electronic Product Code) identifier in hexadecimal format
    /// </summary>
    public string EpcId { get; set; } = string.Empty;

    /// <summary>
    /// EPC ID converted to ASCII representation (if applicable)
    /// </summary>
    public string EpcAscii { get; set; } = string.Empty;

    /// <summary>
    /// Tag Protocol Control word (metadata about tag capabilities)
    /// </summary>
    public string TagPC { get; set; } = string.Empty;

    /// <summary>
    /// Received Signal Strength Indicator (signal quality)
    /// </summary>
    public string RSSI { get; set; } = string.Empty;

    /// <summary>
    /// Name of the antenna that detected the tag
    /// </summary>
    public string AntennaName { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the tag was detected
    /// </summary>
    public string DetectedTime { get; set; } = string.Empty;

    /// <summary>
    /// Status of memory read operation (True/False/Error)
    /// </summary>
    public string ReadSuccess { get; set; } = string.Empty;

    /// <summary>
    /// Data read from EPC memory bank
    /// </summary>
    public string EpcMemoryData { get; set; } = string.Empty;

    /// <summary>
    /// Data read from TID (Tag Identifier) memory bank
    /// </summary>
    public string TidMemoryData { get; set; } = string.Empty;

    /// <summary>
    /// Tag allocation class from TID (first byte)
    /// </summary>
    public string AllocationClass { get; set; } = string.Empty;

    /// <summary>
    /// Manufacturer code from TID memory
    /// </summary>
    public string ManufacturerCode { get; set; } = string.Empty;

    /// <summary>
    /// Decoded manufacturer name
    /// </summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// Error message if read operation failed
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;
}