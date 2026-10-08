namespace JRunner.Core.Contracts;

/// <summary>
/// Process exit codes shared by native JRunner operations.
/// </summary>
public enum ExitCode
{
    Success = 0,
    CompletedNegativeResult = 1,
    Usage = 2,
    InvalidData = 3,
    MissingPrerequisite = 4,
    DeviceUnavailable = 5,
    InputOutput = 6,
    ExternalProcess = 7,
    Cancelled = 130,
}
