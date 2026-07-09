using Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Examples;

/// <summary>
/// Examples of using the RFID transport service
/// </summary>
public static class TransportUsageExamples
{
    /// <summary>
    /// Default RFID reader host - update this to your reader's IP address
    /// </summary>
    public static string DefaultHost { get; set; } = "192.168.1.10";

    /// <summary>
    /// Default RFID reader port - update this to your reader's port
    /// </summary>
    public static int DefaultPort { get; set; } = 10001;

    /// <summary>
    /// Example 1: Basic usage without dependency injection
    /// </summary>
    public static async Task BasicUsageExample()
    {
        await using var transport = new RfidTcpTransportService();

        // Configure connection
        var config = new TransportConfiguration
        {
            Host = DefaultHost,
            Port = DefaultPort,
            DefaultTimeout = TimeSpan.FromSeconds(5),
            EnableDiagnostics = true
        };

        // Subscribe to events
        transport.AsyncMessageReceived += (sender, args) =>
        {
            Console.WriteLine($"Async message received: {args.MessageType}");
        };

        transport.ConnectionStateChanged += (sender, args) =>
        {
            Console.WriteLine($"Connection state: {args.IsConnected} - {args.Message}");
        };

        // Connect
        await transport.ConnectAsync(config);

        // Send command with response
        var commandXml = "<frame><cmd><id>1</id><readTagIDs><sourceName>Source01</sourceName><duration>1000</duration></readTagIDs></cmd></frame>";
        var response = await transport.SendCommandAsync(commandXml, "1", TimeSpan.FromSeconds(10));

        Console.WriteLine($"Response: {response}");

        // Disconnect
        await transport.DisconnectAsync();
    }

    /// <summary>
    /// Example 2: Using dependency injection
    /// </summary>
    public static async Task DependencyInjectionExample()
    {
        var services = new ServiceCollection();

        // Add logging
        services.AddLogging(builder => builder.AddConsole());

        // Add transport service with configuration
        var transportConfig = new TransportConfiguration
        {
            Host = DefaultHost,
            Port = DefaultPort,
            MaxConcurrentCommands = 10,
            EnableDiagnostics = true
        };

        services.AddSingleton(transportConfig);
        services.AddRfidTransportService();

        var serviceProvider = services.BuildServiceProvider();

        // Get the transport service
        var transport = serviceProvider.GetRequiredService<IRfidTransportService>();

        await transport.ConnectAsync(transportConfig);

        // Use the service...

        await transport.DisconnectAsync();
    }

    /// <summary>
    /// Example 3: Error handling and retry logic
    /// </summary>
    public static async Task ErrorHandlingExample()
    {
        await using var transport = new RfidTcpTransportService();

        var config = new TransportConfiguration
        {
            Host = DefaultHost,
            Port = DefaultPort,
            DefaultTimeout = TimeSpan.FromSeconds(5)
        };

        try
        {
            await transport.ConnectAsync(config);

            var commandXml = "<frame><cmd><id>1</id><heartBeat/></cmd></frame>";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await transport.SendCommandAsync(commandXml, "1", cancellationToken: cts.Token);

            Console.WriteLine("Command successful");
        }
        catch (RfidTransportException ex)
        {
            Console.WriteLine($"Transport error: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine($"Timeout: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Operation cancelled");
        }
        finally
        {
            await transport.DisconnectAsync();
        }
    }

    /// <summary>
    /// Example 4: Handling async messages (reports and alarms)
    /// </summary>
    public static async Task AsyncMessageHandlingExample()
    {
        await using var transport = new RfidTcpTransportService();

        // Subscribe to async messages
        transport.AsyncMessageReceived += async (sender, args) =>
        {
            switch (args.MessageType)
            {
                case AsyncMessageType.Report:
                    Console.WriteLine("Tag event report received");
                    await ProcessTagReport(args.XmlMessage);
                    break;

                case AsyncMessageType.Alarm:
                    Console.WriteLine("Alarm received");
                    await ProcessAlarm(args.XmlMessage);
                    break;

                case AsyncMessageType.Notification:
                    Console.WriteLine("Notification received");
                    break;
            }
        };

        var config = new TransportConfiguration
        {
            Host = DefaultHost,
            Port = DefaultPort,
            AcknowledgeAsyncMessages = true
        };

        await transport.ConnectAsync(config);

        // Keep connection alive to receive async messages
        await Task.Delay(TimeSpan.FromMinutes(5));

        await transport.DisconnectAsync();
    }

    /// <summary>
    /// Example 5: Multiple concurrent commands
    /// </summary>
    public static async Task ConcurrentCommandsExample()
    {
        await using var transport = new RfidTcpTransportService();

        var config = new TransportConfiguration
        {
            Host = DefaultHost,
            Port = DefaultPort,
            MaxConcurrentCommands = 5
        };

        await transport.ConnectAsync(config);

        var tasks = new List<Task<string>>();

        for (int i = 1; i <= 5; i++)
        {
            var commandId = i.ToString();
            var commandXml = $"<frame><cmd><id>{commandId}</id><heartBeat/></cmd></frame>";

            tasks.Add(transport.SendCommandAsync(commandXml, commandId));
        }

        var responses = await Task.WhenAll(tasks);

        Console.WriteLine($"Received {responses.Length} responses");

        await transport.DisconnectAsync();
    }

    private static Task ProcessTagReport(string xml)
    {
        // Parse and process tag report
        return Task.CompletedTask;
    }

    private static Task ProcessAlarm(string xml)
    {
        // Parse and process alarm
        return Task.CompletedTask;
    }
}