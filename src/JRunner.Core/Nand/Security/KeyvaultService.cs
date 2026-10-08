using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;

namespace JRunner.Core.Nand.Security;

/// <summary>
/// Distinguishes an already decrypted legacy keyvault from an encrypted one.
/// </summary>
public enum KeyvaultStorageState
{
    /// <summary>
    /// The legacy zero verification range is present in the supplied bytes.
    /// </summary>
    Decrypted,

    /// <summary>
    /// The supplied bytes require a CPU key before metadata can be trusted.
    /// </summary>
    Encrypted,
}

/// <summary>
/// Describes the result of applying a CPU key to a legacy keyvault.
/// </summary>
public enum KeyvaultCpuKeyVerificationStatus
{
    /// <summary>
    /// The supplied keyvault was already decrypted, so no CPU-key proof was necessary.
    /// </summary>
    NotRequired,

    /// <summary>
    /// The keyvault appears encrypted and no CPU key was supplied.
    /// </summary>
    NotAttempted,

    /// <summary>
    /// The decrypted legacy zero verification range matched.
    /// </summary>
    Verified,

    /// <summary>
    /// The supplied CPU key did not produce the legacy zero verification range.
    /// </summary>
    Failed,
}

/// <summary>
/// Identifies the two legacy keyvault layouts inferred from their type marker.
/// </summary>
public enum KeyvaultType
{
    /// <summary>
    /// The type marker is all zeroes or all <c>0xFF</c> bytes.
    /// </summary>
    Type1 = 1,

    /// <summary>
    /// The type marker contains other bytes.
    /// </summary>
    Type2 = 2,
}

/// <summary>
/// Safe, non-secret keyvault metadata extracted at legacy offsets.
/// </summary>
public sealed record KeyvaultMetadata
{
    internal KeyvaultMetadata(
        string consoleSerial,
        string opticalDriveInquiryString,
        string consoleId,
        string region,
        KeyvaultType type,
        string manufacturingDate,
        bool fcrtFlag)
    {
        ConsoleSerial = consoleSerial;
        OpticalDriveInquiryString = opticalDriveInquiryString;
        ConsoleId = consoleId;
        Region = region;
        Type = type;
        ManufacturingDate = manufacturingDate;
        FcrtFlag = fcrtFlag;
    }

    /// <summary>
    /// Gets the ASCII console serial field at keyvault offset <c>0xB0</c>.
    /// </summary>
    public string ConsoleSerial { get; }

    /// <summary>
    /// Gets the ASCII optical-drive inquiry string at keyvault offset <c>0xC92</c>.
    /// </summary>
    public string OpticalDriveInquiryString { get; }

    /// <summary>
    /// Gets the five-byte hexadecimal console identifier at keyvault offset <c>0x9CA</c>.
    /// </summary>
    public string ConsoleId { get; }

    /// <summary>
    /// Gets the two-byte hexadecimal region value at keyvault offset <c>0xC8</c>.
    /// </summary>
    public string Region { get; }

    /// <summary>
    /// Gets the legacy type inferred from the eight-byte marker at offset <c>0x1DF8</c>.
    /// </summary>
    public KeyvaultType Type { get; }

    /// <summary>
    /// Gets the ASCII manufacturing-date field at keyvault offset <c>0x9E4</c>.
    /// </summary>
    public string ManufacturingDate { get; }

    /// <summary>
    /// Gets whether legacy FCRT flag bits <c>0x120</c> are set in the big-endian word at offset <c>0x1C</c>.
    /// </summary>
    public bool FcrtFlag { get; }
}

/// <summary>
/// Immutable safe result from inspecting a legacy keyvault.
/// </summary>
public sealed record KeyvaultInspection
{
    internal KeyvaultInspection(
        KeyvaultStorageState storageState,
        KeyvaultCpuKeyVerificationStatus cpuKeyVerification,
        KeyvaultMetadata? metadata)
    {
        StorageState = storageState;
        CpuKeyVerification = cpuKeyVerification;
        Metadata = metadata;
    }

    /// <summary>
    /// Gets whether the supplied data was already decrypted or required a CPU key.
    /// </summary>
    public KeyvaultStorageState StorageState { get; }

    /// <summary>
    /// Gets the CPU-key verification outcome.
    /// </summary>
    public KeyvaultCpuKeyVerificationStatus CpuKeyVerification { get; }

    /// <summary>
    /// Gets safe metadata only when the keyvault is already decrypted or the CPU key verified it.
    /// </summary>
    public KeyvaultMetadata? Metadata { get; }

    /// <summary>
    /// Gets whether <see cref="Metadata"/> is trusted legacy keyvault metadata.
    /// </summary>
    public bool IsVerified => Metadata is not null;
}

/// <summary>
/// Decrypts, verifies, and extracts safe metadata from fixed-size legacy Xbox keyvaults.
/// </summary>
public static class KeyvaultService
{
    /// <summary>
    /// Gets the exact byte count of a raw legacy keyvault.
    /// </summary>
    public const int KeyvaultLength = 0x4000;

    private const int HeaderLength = 0x10;
    private const int VerificationOffset = 0x40;
    private const int VerificationLength = 0x20;
    private const int ConsoleSerialOffset = 0xB0;
    private const int ConsoleSerialLength = 0x0C;
    private const int RegionOffset = 0xC8;
    private const int RegionLength = 0x02;
    private const int ConsoleIdOffset = 0x9CA;
    private const int ConsoleIdLength = 0x05;
    private const int ManufacturingDateOffset = 0x9E4;
    private const int ManufacturingDateLength = 0x08;
    private const int OpticalDriveInquiryOffset = 0xC92;
    private const int OpticalDriveInquiryLength = 0x1C;
    private const int TypeMarkerOffset = 0x1DF8;
    private const int TypeMarkerLength = 0x08;
    private const int FcrtFlagsOffset = 0x1C;
    private const ushort FcrtMask = 0x0120;

    /// <summary>
    /// Decrypts a fixed-size legacy keyvault with the legacy HMAC-SHA1 and RC4 construction.
    /// </summary>
    /// <param name="rawKeyvault">Exactly <c>0x4000</c> encrypted keyvault bytes.</param>
    /// <param name="cpuKey">The CPU key used as the HMAC key.</param>
    /// <param name="cancellationToken">Cancels before a decrypted result is returned.</param>
    /// <returns>A separately allocated decrypted keyvault. Its first 16 nonce bytes are retained verbatim.</returns>
    /// <exception cref="OperationFailureException">The keyvault size or CPU-key value is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static byte[] Decrypt(
        ReadOnlySpan<byte> rawKeyvault,
        CpuKey cpuKey,
        CancellationToken cancellationToken = default)
    {
        ValidateKeyvaultLength(rawKeyvault);
        EnsureInitializedCpuKey(cpuKey);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] decrypted = GC.AllocateUninitializedArray<byte>(KeyvaultLength);
        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        bool completed = false;

        try
        {
            cpuKey.CopyTo(keyBytes);
            XeCrypt.HmacSha1Truncated(keyBytes, rawKeyvault[..HeaderLength], rc4Key);

            rawKeyvault[..HeaderLength].CopyTo(decrypted);
            rawKeyvault[HeaderLength..].CopyTo(decrypted.AsSpan(HeaderLength));
            Rc4.TransformInPlace(rc4Key, decrypted.AsSpan(HeaderLength), cancellationToken);

            completed = true;
            return decrypted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(rc4Key);
            if (!completed)
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
        }
    }

    /// <summary>
    /// Inspects a fixed-size raw keyvault without retaining decrypted secret material in the result.
    /// </summary>
    /// <param name="rawKeyvault">Exactly <c>0x4000</c> raw keyvault bytes.</param>
    /// <param name="cpuKey">An optional parsed CPU key.</param>
    /// <param name="cancellationToken">Cancels the RC4 transformation when decryption is needed.</param>
    /// <returns>Trusted safe metadata only after legacy zero-range verification succeeds.</returns>
    /// <exception cref="OperationFailureException">The keyvault size or CPU-key value is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static KeyvaultInspection Inspect(
        ReadOnlySpan<byte> rawKeyvault,
        CpuKey? cpuKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateKeyvaultLength(rawKeyvault);
        cancellationToken.ThrowIfCancellationRequested();

        if (HasLegacyVerificationRangeCore(rawKeyvault))
        {
            return new KeyvaultInspection(
                KeyvaultStorageState.Decrypted,
                KeyvaultCpuKeyVerificationStatus.NotRequired,
                ExtractMetadataCore(rawKeyvault));
        }

        if (!cpuKey.HasValue)
        {
            return new KeyvaultInspection(
                KeyvaultStorageState.Encrypted,
                KeyvaultCpuKeyVerificationStatus.NotAttempted,
                metadata: null);
        }

        EnsureInitializedCpuKey(cpuKey.Value);
        byte[] decrypted = Decrypt(rawKeyvault, cpuKey.Value, cancellationToken);
        try
        {
            if (!HasLegacyVerificationRangeCore(decrypted))
            {
                return new KeyvaultInspection(
                    KeyvaultStorageState.Encrypted,
                    KeyvaultCpuKeyVerificationStatus.Failed,
                    metadata: null);
            }

            return new KeyvaultInspection(
                KeyvaultStorageState.Encrypted,
                KeyvaultCpuKeyVerificationStatus.Verified,
                ExtractMetadataCore(decrypted));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    /// <summary>
    /// Verifies a keyvault with a CPU key and returns only safe metadata.
    /// </summary>
    /// <param name="rawKeyvault">Exactly <c>0x4000</c> raw keyvault bytes.</param>
    /// <param name="cpuKey">The CPU key used for verification when decryption is needed.</param>
    /// <param name="cancellationToken">Cancels the RC4 transformation when decryption is needed.</param>
    /// <returns>Trusted safe metadata.</returns>
    /// <exception cref="OperationFailureException">The keyvault is malformed or the CPU key does not verify it.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static KeyvaultMetadata VerifyAndExtract(
        ReadOnlySpan<byte> rawKeyvault,
        CpuKey cpuKey,
        CancellationToken cancellationToken = default)
    {
        KeyvaultInspection inspection = Inspect(rawKeyvault, cpuKey, cancellationToken);
        KeyvaultMetadata? metadata = inspection.Metadata;
        if (metadata is not null)
        {
            return metadata;
        }

        throw new OperationFailureException(
            ExitCode.InvalidData,
            "cpu-key-verification-failed",
            "The CPU key does not verify this keyvault.");
    }

    /// <summary>
    /// Determines whether a fixed-size keyvault has the legacy decrypted zero verification range.
    /// </summary>
    /// <param name="keyvault">Exactly <c>0x4000</c> keyvault bytes.</param>
    /// <returns><see langword="true"/> only when bytes <c>0x40..0x5F</c> are all zero.</returns>
    /// <exception cref="OperationFailureException"><paramref name="keyvault"/> is not exactly <c>0x4000</c> bytes.</exception>
    public static bool HasLegacyVerificationRange(ReadOnlySpan<byte> keyvault)
    {
        ValidateKeyvaultLength(keyvault);
        return HasLegacyVerificationRangeCore(keyvault);
    }

    /// <summary>
    /// Extracts safe metadata from a verified decrypted keyvault.
    /// </summary>
    /// <param name="decryptedKeyvault">Exactly <c>0x4000</c> decrypted keyvault bytes.</param>
    /// <returns>Safe metadata fields; the DVD key is intentionally never extracted.</returns>
    /// <exception cref="OperationFailureException">The keyvault size is invalid or its legacy verification range is absent.</exception>
    public static KeyvaultMetadata ExtractMetadata(ReadOnlySpan<byte> decryptedKeyvault)
    {
        ValidateKeyvaultLength(decryptedKeyvault);
        if (!HasLegacyVerificationRangeCore(decryptedKeyvault))
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "keyvault-verification-failed",
                "The keyvault does not contain the legacy decrypted verification range.");
        }

        return ExtractMetadataCore(decryptedKeyvault);
    }

    private static KeyvaultMetadata ExtractMetadataCore(ReadOnlySpan<byte> keyvault)
    {
        KeyvaultType type = IsAllValue(keyvault.Slice(TypeMarkerOffset, TypeMarkerLength), byte.MaxValue) ||
            IsAllValue(keyvault.Slice(TypeMarkerOffset, TypeMarkerLength), 0)
            ? KeyvaultType.Type1
            : KeyvaultType.Type2;

        ushort fcrtFlags = BinaryPrimitives.ReadUInt16BigEndian(keyvault.Slice(FcrtFlagsOffset, sizeof(ushort)));
        return new KeyvaultMetadata(
            Encoding.ASCII.GetString(keyvault.Slice(ConsoleSerialOffset, ConsoleSerialLength)),
            Encoding.ASCII.GetString(keyvault.Slice(OpticalDriveInquiryOffset, OpticalDriveInquiryLength)),
            Convert.ToHexString(keyvault.Slice(ConsoleIdOffset, ConsoleIdLength)),
            Convert.ToHexString(keyvault.Slice(RegionOffset, RegionLength)),
            type,
            Encoding.ASCII.GetString(keyvault.Slice(ManufacturingDateOffset, ManufacturingDateLength)),
            (fcrtFlags & FcrtMask) != 0);
    }

    private static bool HasLegacyVerificationRangeCore(ReadOnlySpan<byte> keyvault)
    {
        return IsAllValue(keyvault.Slice(VerificationOffset, VerificationLength), 0);
    }

    private static bool IsAllValue(ReadOnlySpan<byte> bytes, byte expected)
    {
        byte difference = 0;
        foreach (byte value in bytes)
        {
            difference |= (byte)(value ^ expected);
        }

        return difference == 0;
    }

    private static void ValidateKeyvaultLength(ReadOnlySpan<byte> keyvault)
    {
        if (keyvault.Length != KeyvaultLength)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "invalid-keyvault-size",
                "A keyvault must be exactly 0x4000 bytes.");
        }
    }

    private static void EnsureInitializedCpuKey(CpuKey cpuKey)
    {
        if (!cpuKey.IsInitialized)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "invalid-cpu-key",
                "A parsed CPU key is required for keyvault decryption.");
        }
    }
}
