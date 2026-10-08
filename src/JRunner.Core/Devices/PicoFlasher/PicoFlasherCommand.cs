namespace JRunner.Core.Devices.PicoFlasher;

/// <summary>
/// Defines the byte opcodes accepted by the shared PicoFlasher and BlackPill command CDC protocol.
/// </summary>
public enum PicoFlasherCommand : byte
{
    /// <summary>
    /// Requests the firmware version as a four-byte little-endian unsigned integer.
    /// </summary>
    GetVersion = 0x00,

    /// <summary>
    /// Requests the flash configuration as a four-byte little-endian unsigned integer.
    /// </summary>
    GetFlashConfiguration = 0x01,

    /// <summary>
    /// Reads one NAND data-and-spare record at the logical block address.
    /// </summary>
    ReadFlash = 0x02,

    /// <summary>
    /// Writes one NAND data-and-spare record at the logical block address.
    /// </summary>
    WriteFlash = 0x03,

    /// <summary>
    /// Begins a NAND read stream whose record count is the logical block address.
    /// </summary>
    ReadFlashStream = 0x04,

    /// <summary>
    /// Erases the NAND erase block containing the logical block address.
    /// </summary>
    EraseFlash = 0x05,

    /// <summary>
    /// Enables or disables the firmware SMC workaround according to the low bit of the logical block address.
    /// </summary>
    SetSmcWorkaround = 0x20,

    /// <summary>
    /// Stops the console SMC.
    /// </summary>
    StopSmc = 0x21,

    /// <summary>
    /// Starts the console SMC.
    /// </summary>
    StartSmc = 0x22,

    /// <summary>
    /// Sets the BlackPill debug UART baud rate. This command is not implemented by PicoFlasher firmware.
    /// </summary>
    SetDebugUartBaud = 0x23,

    /// <summary>
    /// Detects whether eMMC is present.
    /// </summary>
    EmmcDetect = 0x50,

    /// <summary>
    /// Initializes eMMC access.
    /// </summary>
    EmmcInitialize = 0x51,

    /// <summary>
    /// Reads the raw eMMC CID.
    /// </summary>
    EmmcGetCid = 0x52,

    /// <summary>
    /// Reads the raw eMMC CSD.
    /// </summary>
    EmmcGetCsd = 0x53,

    /// <summary>
    /// Reads the raw eMMC extended CSD.
    /// </summary>
    EmmcGetExtendedCsd = 0x54,

    /// <summary>
    /// Reads one eMMC sector at the logical block address.
    /// </summary>
    EmmcRead = 0x55,

    /// <summary>
    /// Begins an eMMC read stream whose sector count is the logical block address.
    /// </summary>
    EmmcReadStream = 0x56,

    /// <summary>
    /// Writes one eMMC sector at the logical block address.
    /// </summary>
    EmmcWrite = 0x57,

    /// <summary>
    /// Reboots the device into its bootloader.
    /// </summary>
    RebootToBootloader = 0xFE,
}
