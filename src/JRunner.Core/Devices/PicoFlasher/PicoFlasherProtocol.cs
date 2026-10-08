using System.Buffers.Binary;

namespace JRunner.Core.Devices.PicoFlasher;

/// <summary>
/// Defines the fixed framing, identity, and response dimensions of the shared PicoFlasher and BlackPill command protocol.
/// </summary>
public static class PicoFlasherProtocol
{
    /// <summary>
    /// Gets the USB vendor identifier advertised by PicoFlasher and BlackPill devices.
    /// </summary>
    public const ushort VendorId = 0x600D;

    /// <summary>
    /// Gets the USB product identifier advertised by PicoFlasher and BlackPill devices.
    /// </summary>
    public const ushort ProductId = 0x7001;

    /// <summary>
    /// Gets the CDC interface number that carries binary commands.
    /// </summary>
    public const int CommandCdcInterfaceNumber = 0;

    /// <summary>
    /// Gets the baud rate used to open the command CDC interface.
    /// </summary>
    public const int CommandBaudRate = 115_200;

    /// <summary>
    /// Gets the minimum firmware version supported by the native PicoFlasher policy.
    /// </summary>
    public const uint MinimumSupportedFirmwareVersion = 4;

    /// <summary>
    /// Gets the default no-progress timeout for native PicoFlasher command transports.
    /// </summary>
    public static readonly TimeSpan DefaultNoProgressTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the longest no-progress timeout supported by the native serial transport.
    /// </summary>
    public static readonly TimeSpan MaximumNoProgressTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Determines whether a no-progress timeout can be represented by the native serial transport.
    /// </summary>
    /// <param name="noProgressTimeout">The timeout to validate.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="noProgressTimeout"/> is positive and representable;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    public static bool IsValidNoProgressTimeout(TimeSpan noProgressTimeout)
    {
        return noProgressTimeout > TimeSpan.Zero && noProgressTimeout <= MaximumNoProgressTimeout;
    }

    /// <summary>
    /// Gets the exact byte count of a packed command frame.
    /// </summary>
    public const int CommandSize = sizeof(byte) + sizeof(uint);

    /// <summary>
    /// Gets the exact byte count of a status response.
    /// </summary>
    public const int StatusSize = sizeof(uint);

    /// <summary>
    /// Gets the number of NAND data bytes in one record.
    /// </summary>
    public const int NandDataSize = 0x200;

    /// <summary>
    /// Gets the number of NAND spare bytes in one record.
    /// </summary>
    public const int NandSpareSize = 0x10;

    /// <summary>
    /// Gets the number of NAND data-and-spare bytes in one wire record.
    /// </summary>
    public const int NandWireRecordSize = NandDataSize + NandSpareSize;

    /// <summary>
    /// Gets the number of bytes in one eMMC sector.
    /// </summary>
    public const int EmmcSectorSize = 0x200;

    /// <summary>
    /// Gets the byte count of a raw eMMC CID response.
    /// </summary>
    public const int EmmcCidSize = 0x10;

    /// <summary>
    /// Gets the byte count of a raw eMMC CSD response.
    /// </summary>
    public const int EmmcCsdSize = 0x10;

    /// <summary>
    /// Gets the byte count of a raw eMMC extended-CSD response.
    /// </summary>
    public const int EmmcExtendedCsdSize = 0x200;

    /// <summary>
    /// Gets the byte offset of the little-endian eMMC EXT_CSD SEC_COUNT field.
    /// </summary>
    public const int EmmcExtendedCsdSectorCountOffset = 0xD4;

    /// <summary>
    /// Gets the number of eMMC sectors in the legacy 48 MiB read window.
    /// </summary>
    public const uint EmmcLegacyReadWindowSectorCount = 0x18000;

    /// <summary>
    /// Gets the exclusive upper eMMC sector-count boundary supported by the firmware address space.
    /// </summary>
    public const uint EmmcMaximumSupportedSectorCountExclusive = 0x800000;

    /// <summary>
    /// Gets the SMC stop wait in milliseconds.
    /// </summary>
    public const int SmcStopWaitMilliseconds = 500;

    /// <summary>
    /// Gets the required wait after stopping the SMC before issuing a flash configuration or data command.
    /// </summary>
    public static readonly TimeSpan SmcStopWait = TimeSpan.FromMilliseconds(SmcStopWaitMilliseconds);

    /// <summary>
    /// Writes one packed command frame into a caller-provided buffer.
    /// </summary>
    /// <param name="destination">The destination buffer, which must be exactly <see cref="CommandSize"/> bytes.</param>
    /// <param name="command">The command opcode to write.</param>
    /// <param name="logicalBlockAddress">The command's little-endian logical block address or command-specific argument.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not exactly <see cref="CommandSize"/> bytes.</exception>
    public static void WriteCommand(
        Span<byte> destination,
        PicoFlasherCommand command,
        uint logicalBlockAddress)
    {
        if (destination.Length != CommandSize)
        {
            throw new ArgumentException(
                $"A PicoFlasher command destination must be exactly {CommandSize} bytes.",
                nameof(destination));
        }

        destination[0] = (byte)command;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[sizeof(byte)..], logicalBlockAddress);
    }

    /// <summary>
    /// Reads a little-endian status value from an exact-size firmware response.
    /// </summary>
    /// <param name="source">The source buffer, which must be exactly <see cref="StatusSize"/> bytes.</param>
    /// <returns>The decoded unsigned firmware status value.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not exactly <see cref="StatusSize"/> bytes.</exception>
    public static uint ReadStatus(ReadOnlySpan<byte> source)
    {
        if (source.Length != StatusSize)
        {
            throw new ArgumentException(
                $"A PicoFlasher status source must be exactly {StatusSize} bytes.",
                nameof(source));
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(source);
    }

    /// <summary>
    /// Reads the little-endian SEC_COUNT value from an exact-size eMMC EXT_CSD response.
    /// </summary>
    /// <param name="source">The source buffer, which must be exactly <see cref="EmmcExtendedCsdSize"/> bytes.</param>
    /// <returns>The raw eMMC sector count.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not exactly <see cref="EmmcExtendedCsdSize"/> bytes.</exception>
    public static uint ReadEmmcExtendedCsdSectorCount(ReadOnlySpan<byte> source)
    {
        if (source.Length != EmmcExtendedCsdSize)
        {
            throw new ArgumentException(
                $"An eMMC EXT_CSD source must be exactly {EmmcExtendedCsdSize} bytes.",
                nameof(source));
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(
            source.Slice(EmmcExtendedCsdSectorCountOffset, sizeof(uint)));
    }
}
