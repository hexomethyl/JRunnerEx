using JRunner.Core.Contracts;
using Xunit;

namespace JRunner.Core.Tests;

public sealed class OperationContractsTests
{
    [Fact]
    public void Failure_contract_rejects_non_failure_exit_codes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OperationFailure(ExitCode.Success, "invalid-image", "Image is invalid."));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OperationFailure(ExitCode.Cancelled, "cancelled", "Operation was cancelled."));
    }

    [Fact]
    public void Failure_contract_preserves_stable_error_fields()
    {
        var failure = new OperationFailure(
            ExitCode.InvalidData,
            "invalid-image",
            "Image data is invalid.");

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("invalid-image", failure.Kind);
        Assert.Equal("Image data is invalid.", failure.Message);
    }

    [Fact]
    public void Negative_result_is_the_only_nonzero_completed_result()
    {
        var result = OperationResult.Negative(new { Equal = false });

        Assert.Equal(ExitCode.CompletedNegativeResult, result.ExitCode);
        Assert.False(result.Result.Equal);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OperationResult<object>(new object(), ExitCode.InvalidData));
    }

    [Fact]
    public void Progress_contract_rejects_impossible_progress()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OperationProgress("reading-image", "Reading image.", completed: 2, total: 1));
    }
}
