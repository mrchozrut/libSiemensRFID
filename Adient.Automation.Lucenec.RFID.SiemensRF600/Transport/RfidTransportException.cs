namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;

/// <summary>
/// Exception thrown by the RFID transport service
/// </summary>
public class RfidTransportException : Exception
{
    public RfidTransportException()
    {
    }

    public RfidTransportException(string message) : base(message)
    {
    }

    public RfidTransportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}