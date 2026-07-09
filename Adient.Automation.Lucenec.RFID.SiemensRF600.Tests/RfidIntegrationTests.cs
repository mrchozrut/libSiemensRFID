using Adient.Automation.Lucenec.RFID.SiemensRF600;
using Adient.Automation.Lucenec.RFID.SiemensRF600.Models;
using Adient.Automation.Lucenec.RFID.SiemensRF600.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Tests;

/// <summary>
/// Integration tests for Siemens RF600 RFID Reader
/// These tests require actual hardware to be connected and configured
/// Tests must run sequentially as RFID operations have dependencies
/// </summary>
public class RfidIntegrationTests : IAsyncLifetime
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
    /// </summary>
    private static readonly string[] SUPPORTED_VERSIONS = new[] { "V2.0", "V2.1", "V2.2", "V2.3", "V3.0", "V3.1" };

    /// <summary>
    /// Delay between major steps (in milliseconds)
    /// </summary>
    private const int RFID_READ_TIMEOUT_MS = 500;

    /// <summary>
    /// Delay between major test steps (in milliseconds)
    /// </summary>
    private const int STEP_DELAY_MS = 1000;

    /// <summary>
    /// Delay between individual tag reads (in milliseconds)
    /// </summary>
    private const int TAG_READ_DELAY_MS = 100;

    private readonly ITestOutputHelper _output;
    private SiemensRf600Client? _client;

    public RfidIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _client = new SiemensRf600Client(RFID_HOST, RFID_PORT);
        _client.EnableDiagnostics = true;
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_client != null)
        {
            try
            {
                await _client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cleanup error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Complete RFID Reader Integration Test
    /// Executes all operations in the correct sequential order:
    /// 1. Connect
    /// 2. Host Greetings
    /// 3. Read Tag IDs
    /// 4. Read Tag Memory (EPC + TID)
    /// 5. Host Goodbye
    /// 6. Disconnect
    /// </summary>
    [Fact]
    public async Task CompleteRfidWorkflow_AllStepsSequential_ShouldSucceed()
    {
        var detectedTags = new List<TagInfo>();

        try
        {
            // ═══════════════════════════════════════════════════════════
            // STEP 1: CONNECT TO RFID READER
            // ═══════════════════════════════════════════════════════════
            _output.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            _output.WriteLine("║ STEP 1: Connecting to RFID Reader                        ║");
            _output.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            _output.WriteLine($"Host: {RFID_HOST}:{RFID_PORT}");
            _output.WriteLine("Connecting...");
            _output.WriteLine("");

            var connected = await _client!.ConnectAsync();

            Assert.True(connected, "Failed to connect to RFID reader");
            Assert.True(_client.IsConnected, "IsConnected property should be true");

            _output.WriteLine("✓ Connected successfully!");
            _output.WriteLine("");
            await Task.Delay(STEP_DELAY_MS);

            // ═══════════════════════════════════════════════════════════
            // STEP 2: HOST GREETINGS (PROTOCOL NEGOTIATION)
            // ═══════════════════════════════════════════════════════════
            _output.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            _output.WriteLine("║ STEP 2: Sending Host Greetings                           ║");
            _output.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            _output.WriteLine($"Reader Type: {READER_TYPE}");
            _output.WriteLine($"Reader Mode: {READER_MODE}");
            _output.WriteLine($"Supported Versions: {string.Join(", ", SUPPORTED_VERSIONS)}");
            _output.WriteLine("");

            var greetings = await _client.HostGreetingsAsync(
                READER_TYPE,
                SUPPORTED_VERSIONS,
                READER_MODE
            );

            Assert.NotNull(greetings);
            Assert.NotEmpty(greetings.Version);
            Assert.NotEmpty(greetings.ConfigID);

            _output.WriteLine("✓ Host Greetings successful!");
            _output.WriteLine($"  Protocol Version: {greetings.Version}");
            _output.WriteLine($"  Configuration ID: {greetings.ConfigID}");
            _output.WriteLine("");
            await Task.Delay(STEP_DELAY_MS);

            // ═══════════════════════════════════════════════════════════
            // STEP 3: READ TAG IDs (INVENTORY)
            // ═══════════════════════════════════════════════════════════
            _output.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            _output.WriteLine("║ STEP 3: Reading RFID Tags (EPC IDs)                      ║");
            _output.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            _output.WriteLine($"Source: {SOURCE_NAME}");
            _output.WriteLine("Duration: 3000 ms");
            _output.WriteLine("Reading tags...");
            _output.WriteLine("");

            var tags = await _client.ReadTagIdsAsync(SOURCE_NAME, RFID_READ_TIMEOUT_MS);

            Assert.NotNull(tags);
            _output.WriteLine($"✓ Read operation completed!");
            _output.WriteLine($"Tags detected: {tags.Length}");
            _output.WriteLine("");

            if (tags.Length == 0)
            {
                _output.WriteLine("⚠ WARNING: No tags detected!");
                _output.WriteLine("  • Ensure a tag is placed near the antenna");
                _output.WriteLine("  • Check antenna configuration");
                _output.WriteLine($"  • Verify source name '{SOURCE_NAME}' is correct");
                _output.WriteLine("");
                _output.WriteLine("Test will continue with remaining steps...");
                _output.WriteLine("");
            }
            else
            {
                _output.WriteLine("═══════════════════════════════════════════════════════════");
                _output.WriteLine("DETECTED TAGS - EPC IDs");
                _output.WriteLine("═══════════════════════════════════════════════════════════");

                // Process and store detected tags
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

                    // Validate tag format
                    Assert.NotNull(tag.TagId);
                    Assert.NotEmpty(tag.TagId);
                    Assert.Matches(@"^[0-9A-Fa-f]+$", tag.TagId);
                    Assert.True(tag.TagId.Length >= 24, $"Tag {tag.TagId} is too short");
                    Assert.True(tag.TagId.Length % 2 == 0, $"Tag {tag.TagId} has odd length");

                    // Convert to ASCII if possible
                    if (RfidHelpers.IsValidHex(tag.TagId) && tag.TagId.Length % 2 == 0)
                    {
                        try
                        {
                            tagInfo.EpcAscii = RfidHelpers.HexToAscii(tag.TagId);
                        }
                        catch { }
                    }

                    detectedTags.Add(tagInfo);

                    _output.WriteLine("");
                    _output.WriteLine($"📟 Tag #{index}");
                    _output.WriteLine($"├─ EPC ID: {tag.TagId}");
                    _output.WriteLine($"├─ Formatted: {RfidHelpers.FormatEpcId(tag.TagId)}");
                    _output.WriteLine($"├─ Length: {tag.TagId.Length} characters");
                    _output.WriteLine($"├─ Format: Valid Hexadecimal ✓");

                    if (!string.IsNullOrEmpty(tagInfo.EpcAscii))
                    {
                        _output.WriteLine($"├─ ASCII: {tagInfo.EpcAscii}");
                    }

                    if (tag.Fields.Count > 0)
                    {
                        _output.WriteLine($"└─ Additional Fields:");
                        foreach (var (key, value) in tag.Fields)
                        {
                            _output.WriteLine($"   ├─ {key}: {value}");
                        }
                    }
                    else
                    {
                        _output.WriteLine($"└─ (No additional fields)");
                    }
                }

                _output.WriteLine("");
                await Task.Delay(STEP_DELAY_MS);

                // ═══════════════════════════════════════════════════════════
                // STEP 4: READ TAG MEMORY (EPC + TID BANKS)
                // ═══════════════════════════════════════════════════════════
                _output.WriteLine("╔═══════════════════════════════════════════════════════════╗");
                _output.WriteLine("║ STEP 4: Reading Tag Memory (EPC + TID Banks)             ║");
                _output.WriteLine("╚═══════════════════════════════════════════════════════════╝");
                _output.WriteLine($"Reading EPC and TID memory banks for {detectedTags.Count} tag(s)...");
                _output.WriteLine("");

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
                int successCount = 0;
                int failCount = 0;

                for (int i = 0; i < detectedTags.Count; i++)
                {
                    var tagInfo = detectedTags[i];

                    _output.WriteLine($"Reading tag #{tagInfo.Index}: {tagInfo.EpcId}...");

                    try
                    {
                        var memoryTags = await _client.ReadTagMemoryAsync(
                            SOURCE_NAME,
                            tagInfo.EpcId,
                            "",
                            memoryFields
                        );

                        if (memoryTags.Length > 0)
                        {
                            var memTag = memoryTags[0];

                            // Verify tag ID matches
                            Assert.Equal(tagInfo.EpcId, memTag.TagId);

                            // Extract EPC data
                            if (memTag.Fields.ContainsKey("EPC_Data"))
                            {
                                tagInfo.EpcMemoryData = memTag.Fields["EPC_Data"];
                                Assert.NotEmpty(tagInfo.EpcMemoryData);
                                _output.WriteLine($"  ✓ EPC Data: {tagInfo.EpcMemoryData}");
                            }

                            // Extract TID data
                            if (memTag.Fields.ContainsKey("TID_Data"))
                            {
                                tagInfo.TidMemoryData = memTag.Fields["TID_Data"];
                                Assert.NotEmpty(tagInfo.TidMemoryData);

                                // Decode manufacturer info
                                if (tagInfo.TidMemoryData.Length >= 8)
                                {
                                    tagInfo.AllocationClass = tagInfo.TidMemoryData.Substring(0, 2);
                                    tagInfo.ManufacturerCode = tagInfo.TidMemoryData.Substring(2, 6);
                                    tagInfo.Manufacturer = RfidHelpers.DecodeManufacturer(tagInfo.ManufacturerCode);

                                    _output.WriteLine($"  ✓ TID Data: {tagInfo.TidMemoryData}");
                                    _output.WriteLine($"  ✓ Allocation Class: 0x{tagInfo.AllocationClass}");
                                    _output.WriteLine($"  ✓ Manufacturer: {tagInfo.Manufacturer}");
                                }
                            }

                            tagInfo.ReadSuccess = "True";
                            successCount++;
                            _output.WriteLine($"  ✓ Memory read successful");
                        }
                        else
                        {
                            tagInfo.ReadSuccess = "False";
                            failCount++;
                            _output.WriteLine($"  ⚠ No memory data returned");
                        }
                    }
                    catch (Exception ex)
                    {
                        tagInfo.ReadSuccess = "Error";
                        tagInfo.ErrorMessage = ex.Message;
                        failCount++;
                        _output.WriteLine($"  ✗ Error: {ex.Message}");
                    }

                    _output.WriteLine("");
                    await Task.Delay(TAG_READ_DELAY_MS);
                }

                // Display summary
                _output.WriteLine("═══════════════════════════════════════════════════════════");
                _output.WriteLine("MEMORY READ SUMMARY");
                _output.WriteLine("═══════════════════════════════════════════════════════════");
                _output.WriteLine($"Total tags: {detectedTags.Count}");
                _output.WriteLine($"Successfully read: {successCount}");
                _output.WriteLine($"Failed: {failCount}");
                _output.WriteLine("");

                // Assert at least some tags were read successfully
                Assert.True(successCount > 0, "At least one tag should be read successfully");
            }

            await Task.Delay(STEP_DELAY_MS);

            // ═══════════════════════════════════════════════════════════
            // STEP 5: HOST GOODBYE
            // ═══════════════════════════════════════════════════════════
            _output.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            _output.WriteLine("║ STEP 5: Sending Host Goodbye                             ║");
            _output.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            _output.WriteLine("");

            await _client.HostGoodbyeAsync(READER_MODE);

            _output.WriteLine("✓ Host Goodbye sent");
            _output.WriteLine("");
            await Task.Delay(STEP_DELAY_MS);

            // ═══════════════════════════════════════════════════════════
            // STEP 6: DISCONNECT
            // ═══════════════════════════════════════════════════════════
            _output.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            _output.WriteLine("║ STEP 6: Disconnecting                                     ║");
            _output.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            _output.WriteLine("");

            await _client.DisconnectAsync();

            Assert.False(_client.IsConnected, "IsConnected should be false after disconnect");

            _output.WriteLine("✓ Disconnected successfully");
            _output.WriteLine("");

            // ═══════════════════════════════════════════════════════════
            // FINAL SUMMARY
            // ═══════════════════════════════════════════════════════════
            _output.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            _output.WriteLine("║         ✓✓✓ ALL TESTS COMPLETED SUCCESSFULLY ✓✓✓         ║");
            _output.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            _output.WriteLine("");
            _output.WriteLine("FINAL SUMMARY:");
            _output.WriteLine($"  Connection: ✓");
            _output.WriteLine($"  Host Greetings: ✓ (Version: {greetings.Version})");
            _output.WriteLine($"  Tags Detected: {tags.Length}");
            _output.WriteLine($"  Tags Read Successfully: {detectedTags.Count(t => t.ReadSuccess == "True")}");
            _output.WriteLine($"  Host Goodbye: ✓");
            _output.WriteLine($"  Disconnection: ✓");
            _output.WriteLine("");
        }
        catch (RfReaderException ex)
        {
            _output.WriteLine("");
            _output.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            _output.WriteLine("║ RFID READER ERROR                                         ║");
            _output.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            _output.WriteLine($"Error Code: {ex.ResultCode}");
            _output.WriteLine($"Message: {ex.Message}");
            _output.WriteLine($"Cause: {ex.Cause}");
            _output.WriteLine("");
            _output.WriteLine("Last sent command:");
            _output.WriteLine(_client!.LastSentCommand);
            _output.WriteLine("");
            _output.WriteLine("Last received response:");
            _output.WriteLine(_client.LastReceivedResponse);

            throw; // Re-throw to fail the test
        }
        catch (TimeoutException ex)
        {
            _output.WriteLine("");
            _output.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            _output.WriteLine("║ TIMEOUT ERROR                                             ║");
            _output.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            _output.WriteLine($"Message: {ex.Message}");
            _output.WriteLine("");
            _output.WriteLine("Possible causes:");
            _output.WriteLine("  • Reader is not responding");
            _output.WriteLine("  • Network connectivity issues");
            _output.WriteLine("  • Incorrect IP address or port");
            _output.WriteLine("  • Command took longer than expected");

            throw; // Re-throw to fail the test
        }
        catch (Exception ex)
        {
            _output.WriteLine("");
            _output.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            _output.WriteLine("║ UNEXPECTED ERROR                                          ║");
            _output.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            _output.WriteLine($"Type: {ex.GetType().Name}");
            _output.WriteLine($"Message: {ex.Message}");
            _output.WriteLine("");
            _output.WriteLine("Stack trace:");
            _output.WriteLine(ex.StackTrace);

            throw; // Re-throw to fail the test
        }
    }
}