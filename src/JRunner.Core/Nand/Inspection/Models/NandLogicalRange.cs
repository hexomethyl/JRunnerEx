namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// A non-empty, overflow-safe range in the canonical logical NAND projection.
/// </summary>
public readonly record struct NandLogicalRange
{
    /// <summary>
    /// Creates a logical NAND range.
    /// </summary>
    /// <param name="offset">The zero-based logical byte offset.</param>
    /// <param name="length">The positive logical byte length.</param>
    public NandLogicalRange(long offset, long length)
    {
        Validate(offset, length, nameof(offset));
        Offset = offset;
        Length = length;
    }

    /// <summary>
    /// Gets the zero-based logical byte offset.
    /// </summary>
    public long Offset { get; }

    /// <summary>
    /// Gets the positive logical byte length.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets the exclusive logical byte offset immediately after this range.
    /// </summary>
    public long EndExclusive => checked(Offset + Length);

    internal void Validate(string parameterName)
    {
        Validate(Offset, Length, parameterName);
    }

    internal void EnsureFitsWithin(long logicalImageLength, string parameterName)
    {
        if (logicalImageLength < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalImageLength),
                logicalImageLength,
                "The logical image length cannot be negative.");
        }

        Validate(parameterName);
        if (EndExclusive > logicalImageLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The logical range extends beyond the canonical image.");
        }
    }

    private static void Validate(long offset, long length, string parameterName)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, offset, "The logical offset cannot be negative.");
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The logical length must be positive.");
        }

        if (offset > long.MaxValue - length)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The logical offset and length exceed the supported range.");
        }
    }
}
