using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JRunner.Core.Nand.Security;

/// <summary>
/// Holds a parsed Xbox CPU key without retaining its hexadecimal source text or exposing a mutable byte array.
/// </summary>
[JsonConverter(typeof(CpuKeyJsonConverter))]
public readonly struct CpuKey : IEquatable<CpuKey>
{
    /// <summary>
    /// Gets the exact number of key bytes accepted by Xbox NAND cryptography.
    /// </summary>
    public const int ByteLength = 0x10;

    /// <summary>
    /// Gets the exact number of hexadecimal characters in a CPU key.
    /// </summary>
    public const int HexadecimalLength = ByteLength * 2;

    private readonly ulong high;
    private readonly ulong low;
    private readonly bool isInitialized;

    private CpuKey(ulong high, ulong low)
    {
        this.high = high;
        this.low = low;
        isInitialized = true;
    }

    /// <summary>
    /// Gets whether this value was created by a CPU-key factory rather than being the default value.
    /// </summary>
    /// <remarks>
    /// This does not reveal any key material. A parsed all-zero key is initialized and remains distinct from the default value.
    /// </remarks>
    [JsonIgnore]
    public bool IsInitialized => isInitialized;

    /// <summary>
    /// Parses exactly 32 hexadecimal characters as a 16-byte CPU key.
    /// </summary>
    /// <param name="text">The hexadecimal key text.</param>
    /// <returns>A value that can be copied into a caller-provided crypto buffer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="text"/> is not exactly 32 hexadecimal characters.</exception>
    public static CpuKey Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryParse(text.AsSpan(), out CpuKey key))
        {
            throw new FormatException("A CPU key must contain exactly 32 hexadecimal characters.");
        }

        return key;
    }

    /// <summary>
    /// Attempts to parse exactly 32 hexadecimal characters as a CPU key.
    /// </summary>
    /// <param name="text">The hexadecimal key text.</param>
    /// <param name="key">The parsed key on success; otherwise the default value.</param>
    /// <returns><see langword="true"/> only for exactly 32 hexadecimal characters.</returns>
    public static bool TryParse(string? text, out CpuKey key)
    {
        if (text is null)
        {
            key = default;
            return false;
        }

        return TryParse(text.AsSpan(), out key);
    }

    /// <summary>
    /// Attempts to parse exactly 32 hexadecimal characters as a CPU key without allocating an intermediary byte array.
    /// </summary>
    /// <param name="text">The hexadecimal key characters.</param>
    /// <param name="key">The parsed key on success; otherwise the default value.</param>
    /// <returns><see langword="true"/> only for exactly 32 hexadecimal characters.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out CpuKey key)
    {
        if (text.Length != HexadecimalLength)
        {
            key = default;
            return false;
        }

        ulong parsedHigh = 0;
        ulong parsedLow = 0;

        for (int byteIndex = 0; byteIndex < ByteLength; byteIndex++)
        {
            int characterIndex = byteIndex * 2;
            int upper = GetHexValue(text[characterIndex]);
            int lower = GetHexValue(text[characterIndex + 1]);
            if (upper < 0 || lower < 0)
            {
                key = default;
                return false;
            }

            byte value = (byte)((upper << 4) | lower);
            if (byteIndex < sizeof(ulong))
            {
                parsedHigh = (parsedHigh << 8) | value;
            }
            else
            {
                parsedLow = (parsedLow << 8) | value;
            }
        }

        key = new CpuKey(parsedHigh, parsedLow);
        return true;
    }

    /// <summary>
    /// Creates a CPU key from exactly 16 bytes.
    /// </summary>
    /// <param name="bytes">The key bytes.</param>
    /// <returns>An immutable CPU-key value.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public static CpuKey FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException("A CPU key must be exactly 16 bytes.", nameof(bytes));
        }

        return new CpuKey(
            BinaryPrimitives.ReadUInt64BigEndian(bytes[..sizeof(ulong)]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[sizeof(ulong)..]));
    }

    /// <summary>
    /// Copies the key into the first 16 bytes of a caller-owned buffer for immediate cryptographic use.
    /// </summary>
    /// <param name="destination">A buffer containing at least 16 bytes.</param>
    /// <exception cref="InvalidOperationException">This value is the uninitialized default.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public void CopyTo(Span<byte> destination)
    {
        EnsureInitialized();

        if (destination.Length < ByteLength)
        {
            throw new ArgumentException("The CPU-key destination must contain at least 16 bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination[..sizeof(ulong)], high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[sizeof(ulong)..ByteLength], low);
    }

    /// <summary>
    /// Attempts to copy the key into a caller-owned buffer for immediate cryptographic use.
    /// </summary>
    /// <param name="destination">A buffer containing at least 16 bytes.</param>
    /// <returns><see langword="true"/> only when this key is initialized and the destination is large enough.</returns>
    public bool TryCopyTo(Span<byte> destination)
    {
        if (!isInitialized || destination.Length < ByteLength)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination[..sizeof(ulong)], high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[sizeof(ulong)..ByteLength], low);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(CpuKey other)
    {
        return isInitialized == other.isInitialized && high == other.high && low == other.low;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CpuKey other && Equals(other);
    }

    /// <summary>
    /// Does not derive a public value from secret key material.
    /// </summary>
    public override int GetHashCode()
    {
        return isInitialized ? 1 : 0;
    }

    /// <summary>
    /// Returns a stable redacted description rather than hexadecimal key material.
    /// </summary>
    public override string ToString()
    {
        return "CpuKey [redacted]";
    }

    /// <summary>
    /// Tests two CPU-key values for equality.
    /// </summary>
    public static bool operator ==(CpuKey left, CpuKey right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Tests two CPU-key values for inequality.
    /// </summary>
    public static bool operator !=(CpuKey left, CpuKey right)
    {
        return !left.Equals(right);
    }

    private static int GetHexValue(char value)
    {
        return value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'A' and <= 'F' => value - 'A' + 10,
            >= 'a' and <= 'f' => value - 'a' + 10,
            _ => -1,
        };
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException("The CPU key has not been initialized.");
        }
    }
}

/// <summary>
/// Explicitly rejects JSON representation of CPU keys so secret bytes cannot be emitted by an accidental serializer call.
/// </summary>
public sealed class CpuKeyJsonConverter : JsonConverter<CpuKey>
{
    /// <inheritdoc />
    public override CpuKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException("CPU keys must not be represented in JSON.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, CpuKey value, JsonSerializerOptions options)
    {
        throw new NotSupportedException("CPU keys must not be serialized.");
    }
}
