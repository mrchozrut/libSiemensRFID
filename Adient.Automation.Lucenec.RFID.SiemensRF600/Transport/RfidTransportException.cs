namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;

/// <summary>
/// Exception thrown by the RFID transport service
/// </summary>
public class RfidTransportException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RfidTransportException"/> class
    /// </summary>
    public RfidTransportException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RfidTransportException"/> class with a specified error message
    /// </summary>
    /// <param name="message">The message that describes the error</param>
    public RfidTransportException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RfidTransportException"/> class with a specified error message and inner exception
    /// </summary>
    /// <param name="message">The message that describes the error</param>
    /// <param name="innerException">The exception that is the cause of the current exception</param>
    public RfidTransportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}