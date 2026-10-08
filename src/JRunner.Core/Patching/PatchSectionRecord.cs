using System.Collections.Immutable;

namespace JRunner.Core.Patching;

/// <summary>
/// A decoded NAND patch record containing a big-endian destination address and DWORD values.
/// </summary>
public sealed record PatchSectionRecord
{
    /// <summary>
    /// Creates a decoded patch record.
    /// </summary>
    public PatchSectionRecord(uint address, ImmutableArray<uint> values)
    {
        if (address == uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(address), address, "The section terminator cannot be a patch record address.");
        }

        Address = address;
        Values = values.IsDefault ? ImmutableArray<uint>.Empty : values;
    }

    /// <summary>
    /// Gets the patch destination address.
    /// </summary>
    public uint Address { get; }

    /// <summary>
    /// Gets patch values in their encoded order.
    /// </summary>
    public ImmutableArray<uint> Values { get; }

    /// <summary>
    /// Gets the number of DWORD values in this record.
    /// </summary>
    public uint Count => checked((uint)Values.Length);
}
