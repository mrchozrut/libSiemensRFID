using Adient.Automation.Lucenec.RFID.SiemensRF600;
using Adient.Automation.Lucenec.RFID.SiemensRF600.Models;
using Adient.Automation.Lucenec.RFID.SiemensRF600.Utilities;

namespace Adient.Automation.Lucenec.RFID.SiemensRF600.ConsoleTest;

class Program
{
    // ========================================
    // CONFIGURATION - UPDATE THESE VALUES!
    // ========================================
    private const string RFID_HOST = "192.168.1.10";
    private const int RFID_PORT = 10001;
    private const string SOURCE_NAME = "Readpoint_1";
    private const string READER_TYPE = "SIMATIC_RF680R";
    private const string READER_MODE = "Default";
    
    /// <summary>
    /// Supported protocol versions for host greetings
    /// Common versions: V2.0, V2.1, V2.2, V2.3, V3.0, V3.1
    /// </summary>
    private static readonly string[] SUPPORTED_VERSIONS = new[] { "V2.0", "V2.1", "V2.2", "V2.3", "V3.0", "V3.1" };

    /// <summary>
    /// Delay between major steps (in milliseconds)
    /// </summary>
    private const int RFID_READ_TIMEOUT_MS = 500;

    /// <summary>
    /// Delay between major steps (in milliseconds)
    /// </summary>
    private const int STEP_DELAY_MS = 1000;

    /// <summary>
    /// Delay between individual tag reads (in milliseconds)
    /// </summary>
    private const int TAG_READ_DELAY_MS = 100;

    static async Task Main(string[] args)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  Siemens RF600 RFID Reader - Connection Test            ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        await using var client = new SiemensRf600Client(RFID_HOST, RFID_PORT);

        // Enable diagnostics to see XML messages
        client.EnableDiagnostics = true;

        // Array to store all tag information
        var detectedTags = new List<TagInfo>();

        try
        {
            // ===== STEP 1: CONNECT =====
            Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
            Console.WriteLine("│ STEP 1: Connecting to RFID Reader                      │");
            Console.WriteLine("└─────────────────────────────────────────────────────────┘");
            Console.WriteLine($"Host: {RFID_HOST}:{RFID_PORT}");
            Console.WriteLine("Connecting...");

            var connected = await client.ConnectAsync();

            if (!connected)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("✗ Connection FAILED!");
                Console.ResetColor();
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Connected successfully!");
            Console.ResetColor();
            Console.WriteLine();
            await Task.Delay(STEP_DELAY_MS);

            // ===== STEP 2: HOST GREETINGS =====
            Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
            Console.WriteLine("│ STEP 2: Sending Host Greetings                         │");
            Console.WriteLine("└─────────────────────────────────────────────────────────┘");
            Console.WriteLine($"Reader Type: {READER_TYPE}");
            Console.WriteLine($"Reader Mode: {READER_MODE}");
            Console.WriteLine($"Supported Versions: {string.Join(", ", SUPPORTED_VERSIONS)}");
            Console.WriteLine();

            var greetings = await client.HostGreetingsAsync(
                READER_TYPE,
                SUPPORTED_VERSIONS,
                READER_MODE
            );

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Host Greetings successful!");
            Console.ResetColor();
            Console.WriteLine($"  Protocol Version: {greetings.Version}");
            Console.WriteLine($"  Configuration ID: {greetings.ConfigID}");
            Console.WriteLine();
            await Task.Delay(STEP_DELAY_MS);

            // ===== STEP 3: READ TAG IDs =====
            Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
            Console.WriteLine("│ STEP 3: Reading RFID Tags (EPC IDs)                    │");
            Console.WriteLine("└─────────────────────────────────────────────────────────┘");
            Console.WriteLine($"Source: {SOURCE_NAME}");
            Console.WriteLine("Duration: 3000 ms");
            Console.WriteLine("Reading tags...");
            Console.WriteLine();

            var tags = await client.ReadTagIdsAsync(SOURCE_NAME, RFID_READ_TIMEOUT_MS);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Read operation completed!");
            Console.ResetColor();
            Console.WriteLine($"Tags detected: {tags.Length}");
            Console.WriteLine();

            if (tags.Length > 0)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
                Console.WriteLine("║ DETECTED TAGS - EPC IDs                               ║");
                Console.WriteLine("╚═══════════════════════════════════════════════════════╝");
                Console.ResetColor();

                // Save detected tags to array
                foreach (var (tag, index) in tags.Select((t, i) => (t, i + 1)))
                {
                    var tagInfo = new TagInfo
                    {
                        Index = index,
                        EpcId = tag.TagId,
                        TagPC = tag.Fields.ContainsKey("tagPC") ? tag.Fields["tagPC"] : "",
                        RSSI = tag.Fields.ContainsKey("rssi") ? tag.Fields["rssi"] : "",
                        AntennaName = tag.Fields.ContainsKey("antennaName") ? tag.Fields["antennaName"] : "",
                        DetectedTime = tag.Fields.ContainsKey("utcTime") ? tag.Fields["utcTime"] : DateTime.UtcNow.ToString("O")
                    };

                    detectedTags.Add(tagInfo);

                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"📟 Tag #{index}");
                    Console.ResetColor();
                    Console.WriteLine($"├─ EPC ID: {tag.TagId}");
                    Console.WriteLine($"├─ Length: {tag.TagId.Length} characters");

                    // Validate format
                    var isHex = System.Text.RegularExpressions.Regex.IsMatch(
                        tag.TagId,
                        @"^[0-9A-Fa-f]+$"
                    );
                    Console.WriteLine($"├─ Format: {(isHex ? "Valid Hexadecimal" : "Invalid")}");

                    // Convert hex to ASCII if possible
                    if (isHex && tag.TagId.Length % 2 == 0)
                    {
                        try
                        {
                            var ascii = RfidHelpers.HexToAscii(tag.TagId);
                            Console.WriteLine($"├─ ASCII: {ascii}");
                            tagInfo.EpcAscii = ascii;
                        }
                        catch
                        {
                            Console.WriteLine($"├─ ASCII: (not convertible)");
                        }
                    }

                    if (!string.IsNullOrEmpty(tag.Data))
                    {
                        Console.WriteLine($"├─ Data: {tag.Data}");
                    }

                    if (tag.Fields.Count > 0)
                    {
                        Console.WriteLine($"└─ Additional Fields:");
                        foreach (var (key, value) in tag.Fields)
                        {
                            Console.WriteLine($"   ├─ {key}: {value}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"└─ (No additional fields)");
                    }
                }

                Console.WriteLine();

                // ===== STEP 4: READ TAG MEMORY FOR ALL TAGS =====
                Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
                Console.WriteLine("│ STEP 4: Reading Tag Memory for All Tags                │");
                Console.WriteLine("└─────────────────────────────────────────────────────────┘");
                Console.WriteLine($"Reading EPC and TID memory banks for {detectedTags.Count} tag(s)...");
                Console.WriteLine();

                // Define memory banks to read
                var memoryFields = new[]
                {
                    new RfTagField
                    {
                        Bank = EpcMemoryBank.Epc,
                        Address = 4,
                        Length = 16
                    },
                    new RfTagField
                    {
                        Bank = EpcMemoryBank.Tid,
                        Address = 0,
                        Length = 16
                    }
                };

                // Read memory for each detected tag
                for (int i = 0; i < detectedTags.Count; i++)
                {
                    var tagInfo = detectedTags[i];
                    
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"Reading tag #{tagInfo.Index}: {tagInfo.EpcId}...");
                    Console.ResetColor();

                    try
                    {
                        var memoryTags = await client.ReadTagMemoryAsync(
                            SOURCE_NAME,
                            tagInfo.EpcId,
                            "",
                            memoryFields
                        );

                        if (memoryTags.Length > 0)
                        {
                            var memTag = memoryTags[0];

                            // Extract EPC data
                            if (memTag.Fields.ContainsKey("EPC_Data"))
                            {
                                tagInfo.EpcMemoryData = memTag.Fields["EPC_Data"];
                                Console.WriteLine($"  EPC Data: {tagInfo.EpcMemoryData}");
                            }

                            // Extract TID data
                            if (memTag.Fields.ContainsKey("TID_Data"))
                            {
                                tagInfo.TidMemoryData = memTag.Fields["TID_Data"];
                                
                                // Decode manufacturer info
                                var tidHex = tagInfo.TidMemoryData;
                                if (tidHex.Length >= 8)
                                {
                                    tagInfo.AllocationClass = tidHex.Substring(0, 2);
                                    tagInfo.ManufacturerCode = tidHex.Substring(2, 6);
                                    tagInfo.Manufacturer = RfidHelpers.DecodeManufacturer(tagInfo.ManufacturerCode);
                                    Console.WriteLine($"  TID Data: {tagInfo.TidMemoryData}");
                                    Console.WriteLine($"  Manufacturer: {tagInfo.Manufacturer}");
                                }
                            }

                            // Store success status
                            tagInfo.ReadSuccess = memTag.Fields.ContainsKey("success") 
                                ? memTag.Fields["success"] 
                                : "True";

                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"  ✓ Memory read successful");
                            Console.ResetColor();
                        }
                        else
                        {
                            tagInfo.ReadSuccess = "False";
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠ No memory data returned");
                            Console.ResetColor();
                        }
                    }
                    catch (Exception ex)
                    {
                        tagInfo.ReadSuccess = "Error";
                        tagInfo.ErrorMessage = ex.Message;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ✗ Error: {ex.Message}");
                        Console.ResetColor();
                    }

                    Console.WriteLine();
                    await Task.Delay(TAG_READ_DELAY_MS);
                }

                // Display summary
                Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
                Console.WriteLine("│ Summary                                                 │");
                Console.WriteLine("└─────────────────────────────────────────────────────────┘");
                Console.WriteLine($"Total tags: {detectedTags.Count}");
                Console.WriteLine($"Successfully read: {detectedTags.Count(t => t.ReadSuccess == "True")}");
                Console.WriteLine($"Failed: {detectedTags.Count(t => t.ReadSuccess != "True")}");
                Console.WriteLine();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("⚠ No tags detected in range");
                Console.WriteLine("  • Ensure a tag is placed near the antenna");
                Console.WriteLine("  • Check antenna configuration");
                Console.WriteLine($"  • Verify source name '{SOURCE_NAME}' is correct");
                Console.ResetColor();
                Console.WriteLine();
            }

            await Task.Delay(STEP_DELAY_MS);

            // ===== STEP 5: HOST GOODBYE =====
            Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
            Console.WriteLine("│ STEP 5: Sending Host Goodbye                           │");
            Console.WriteLine("└─────────────────────────────────────────────────────────┘");

            await client.HostGoodbyeAsync(READER_MODE);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Host Goodbye sent");
            Console.ResetColor();
            Console.WriteLine();
            await Task.Delay(1000);

            // ===== STEP 6: DISCONNECT =====
            Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
            Console.WriteLine("│ STEP 6: Disconnecting                                   │");
            Console.WriteLine("└─────────────────────────────────────────────────────────┘");

            await client.DisconnectAsync();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Disconnected successfully");
            Console.ResetColor();
            Console.WriteLine();

            // ===== SUCCESS =====
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║         ✓✓✓ ALL TESTS COMPLETED SUCCESSFULLY ✓✓✓        ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
            Console.ResetColor();
        }
        catch (RfReaderException ex)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║ RFID READER ERROR                                        ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine($"Error Code: {ex.ResultCode}");
            Console.WriteLine($"Message: {ex.Message}");
            Console.WriteLine($"Cause: {ex.Cause}");
            Console.WriteLine();
            Console.WriteLine("Last sent command:");
            Console.WriteLine(client.LastSentCommand);
            Console.WriteLine();
            Console.WriteLine("Last received response:");
            Console.WriteLine(client.LastReceivedResponse);
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║ TIMEOUT ERROR                                            ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine($"Message: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("Possible causes:");
            Console.WriteLine("  • Reader is not responding");
            Console.WriteLine("  • Network connectivity issues");
            Console.WriteLine("  • Incorrect IP address or port");
            Console.WriteLine($"  • Command took longer than expected");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║ UNEXPECTED ERROR                                         ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine($"Type: {ex.GetType().Name}");
            Console.WriteLine($"Message: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("Stack trace:");
            Console.WriteLine(ex.StackTrace);
        }

        Console.WriteLine();
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }
}