namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// A bounded scalar representation of a NAND magic value that never retains source bytes.
/// </summary>
/// <remarks>
/// The value is interpreted as a big-endian integer occupying exactly <see cref="ByteLength"/>
/// bytes. Use <see cref="Hexadecimal"/> when rendering the value.
/// </remarks>
public readonly record struct NandMagic
{
    /// <summary>
    /// Creates a safe magic representation.
    /// </summary>
    /// <param name="value">The big-endian numeric value of the magic bytes.</param>
    /// <param name="byteLength">The number of source bytes represented, from one through four.</param>
    public NandMagic(uint value, byte byteLength)
    {
        Validate(value, byteLength, nameof(value));
        Value = value;
        ByteLength = byteLength;
    }

    /// <summary>
    /// Gets the big-endian numeric value of the represented magic bytes.
    /// </summary>
    public uint Value { get; }

    /// <summary>
    /// Gets the number of represented bytes.
    /// </summary>
    public byte ByteLength { get; }

    /// <summary>
    /// Gets the normalized upper-case hexadecimal display form, including its <c>0x</c> prefix.
    /// </summary>
    public string Hexadecimal => ByteLength switch
    {
        1 => $"0x{Value:X2}",
        2 => $"0x{Value:X4}",
        3 => $"0x{Value:X6}",
        4 => $"0x{Value:X8}",
        _ => throw new InvalidOperationException("The NAND magic representation is invalid."),
    };

    /// <inheritdoc />
    public override string ToString()
    {
        return Hexadecimal;
    }

    internal void Validate(string parameterName)
    {
        Validate(Value, ByteLength, parameterName);
    }

    private static void Validate(uint value, byte byteLength, string parameterName)
    {
        if (byteLength is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteLength),
                byteLength,
                "A NAND magic value must represent from one through four bytes.");
        }

        if (byteLength < 4 && value > ((1u << (byteLength * 8)) - 1))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The NAND magic value does not fit within its declared byte length.");
        }
    }
}
