using JRunner.Core.Contracts;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Preparation;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Validates a completed but unpublished XeBuild image using the bounded NAND inspection surface.
/// </summary>
internal static class XeBuildOutputValidator
{
    private const int BufferSize = 0x10000;

    internal static async Task<NandInspectionResult> ValidateAsync(
        string outputPath,
        XeBuildRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            FileInfo output = new(outputPath);
            if (!output.Exists || output.LinkTarget is not null || output.Length <= 0)
            {
                throw InvalidOutput();
            }

            await using FileStream stream = new(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ValidateAsync(stream, request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OperationFailureException exception) when (exception.Kind != "xebuild-output-invalid")
        {
            throw InvalidOutput();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InvalidOutput();
        }
    }

    /// <summary>
    /// Inspects the same regular-file stream whose identity and bytes the output reservation binds to publication.
    /// The caller retains ownership of the stream.
    /// </summary>
    internal static async Task<NandInspectionResult> ValidateAsync(
        Stream output,
        XeBuildRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            NandInspectionResult inspection = await NandImageService.InspectAsync(
                output,
                request.CpuKey,
                progress,
                cancellationToken).ConfigureAwait(false);
            Validate(inspection, request);
            return inspection;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OperationFailureException exception) when (exception.Kind != "xebuild-output-invalid")
        {
            throw InvalidOutput();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InvalidOutput();
        }
    }

    internal static void Validate(NandInspectionResult inspection, XeBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        Validate(inspection.SemanticEvidence, request);
    }

    internal static void Validate(NandSemanticEvidence evidence, XeBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(request);
        Validate(
            evidence,
            request,
            XeBuildOutputEvidenceCatalog.Get(request.Target.HackType, request.Target.Options.Rgh3));
    }

    internal static void Validate(
        NandSemanticEvidence evidence,
        XeBuildRequest request,
        XeBuildOutputEvidenceCapability capability)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capability);
        XeBuildOutputEvidenceCapability expectedCapability = XeBuildOutputEvidenceCatalog.Get(
            request.Target.HackType,
            request.Target.Options.Rgh3);
        if (capability.HackType != expectedCapability.HackType ||
            capability.RequiredFamily != expectedCapability.RequiredFamily)
        {
            throw InvalidOutput("The output-evidence capability does not describe the requested image family.");
        }

        if (!capability.IsAvailable)
        {
            throw InvalidOutput("Required direct output-evidence corpus is unavailable: ImageFamilyEvidenceUnavailable.");
        }

        var console = request.Target.ConsoleOverride?.Id ?? request.Source.DetectedConsoleId;
        if (console is null)
        {
            throw InvalidOutput("The requested console has no positive inspection evidence.");
        }

        NandSemanticEvidenceValidationResult result = NandSemanticEvidenceValidator.Validate(
            evidence,
            new NandSemanticEvidenceRequirement(
                console.Value,
                request.Target.DashboardVersion,
                capability.RequiredFamily,
                capability.ReviewedManifest));
        if (!result.IsMatch)
        {
            throw InvalidOutput(string.Concat(
                "Required console, exact dashboard, or direct image-family evidence was unavailable, untrusted, absent, conflicting, or mismatched: ",
                string.Join(", ", result.Mismatches.Select(mismatch => mismatch.Kind)),
                "."));
        }
    }

    private static OperationFailureException InvalidOutput(string? detail = null)
    {
        return new OperationFailureException(
            ExitCode.ExternalProcess,
            "xebuild-output-invalid",
            detail ?? "The XeBuild process produced an image that failed structural validation.");
    }
}
