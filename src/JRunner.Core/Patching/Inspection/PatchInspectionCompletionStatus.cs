namespace JRunner.Core.Patching.Inspection;

/// <summary>
/// Describes whether a patch section was structurally complete.
/// </summary>
public enum PatchInspectionCompletionStatus
{
    /// <summary>
    /// The section reached its <c>0xFFFFFFFF</c> terminator without structural errors.
    /// </summary>
    Complete,

    /// <summary>
    /// The section was malformed, truncated, or ended without a terminator. Consult structural
    /// diagnostics for the precise reason.
    /// </summary>
    Malformed,
}
