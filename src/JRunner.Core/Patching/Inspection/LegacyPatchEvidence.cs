namespace JRunner.Core.Patching.Inspection;

/// <summary>
/// Immutable evidence that one decoded patch record matches a legacy J-Runner patch-table
/// signature.
/// </summary>
public sealed record LegacyPatchEvidence
{
    /// <summary>
    /// Creates legacy patch evidence for the first value in a matching patch record.
    /// </summary>
    /// <param name="patchName">The canonical legacy table name.</param>
    /// <param name="recordIndex">The zero-based index in the inspection result's records.</param>
    /// <param name="address">The matching record destination address.</param>
    /// <param name="wordCount">The decoded record word count.</param>
    /// <param name="firstValue">The matching first DWORD value.</param>
    public LegacyPatchEvidence(
        string patchName,
        int recordIndex,
        uint address,
        uint wordCount,
        uint firstValue)
    {
        if (string.IsNullOrWhiteSpace(patchName))
        {
            throw new ArgumentException("A legacy patch name is required.", nameof(patchName));
        }

        if (recordIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recordIndex), recordIndex, "A record index cannot be negative.");
        }

        if (wordCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wordCount), wordCount, "A recognized patch record must contain its first DWORD value.");
        }

        PatchName = patchName;
        RecordIndex = recordIndex;
        Address = address;
        WordCount = wordCount;
        FirstValue = firstValue;
    }

    /// <summary>
    /// Gets the canonical name from the legacy patch table: <c>FuseBlow</c>, <c>XLUSB</c>,
    /// <c>XLHDD</c>, <c>UsbdSec</c>, or <c>CoronaKeyFix</c>.
    /// </summary>
    public string PatchName { get; }

    /// <summary>
    /// Gets the zero-based index in <see cref="PatchInspectionResult.Records"/> of the record
    /// that produced this evidence.
    /// </summary>
    public int RecordIndex { get; }

    /// <summary>
    /// Gets the matched record's destination address.
    /// </summary>
    public uint Address { get; }

    /// <summary>
    /// Gets the matched record's decoded DWORD count.
    /// </summary>
    public uint WordCount { get; }

    /// <summary>
    /// Gets the first decoded DWORD, which matched the legacy patch signature.
    /// </summary>
    public uint FirstValue { get; }
}
