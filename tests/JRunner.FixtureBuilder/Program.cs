using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JRunner.Core.Binary;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;

namespace JRunner.FixtureBuilder;

public static class FixtureGenerator
{
    private const string FixtureSet = "jrunner-native-core";

    private const int SmallBlockCount = 0x400;
    private const int FirstStageOffset = 0x8000;
    private const int SmcOffset = 0x1000;
    private const int SmcLength = 0x2DC0;
    private const int KeyvaultOffset = 0x4000;
    private const int PrimaryPatchOffset = 0xC0010;
    private const int CbStageLength = 0x3C0;

    private const int CurrentWorkingDirectoryFileDescriptor = -100;
    private const uint RenameNoReplace = 1;
    private const uint RenameExclusive = 4;

    private static readonly uint[] LegacyPatchWords =
    [
        0x0000C000U, 1U, 0x38800000U,
        0x000E3A7CU, 1U, 0x3CE02000U,
        0x0015D8ECU, 1U, 0x39401000U,
        0x000D8748U, 2U, 0x38600001U, 0x11223344U,
        0x00003B8CU, 1U, 0x389F0010U,
        uint.MaxValue,
    ];

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath,
        uint flags);

    [DllImport("libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusivePath(string sourcePath, string destinationPath, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFile(string sourcePath, string destinationPath);

    private static int Main(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            Console.Error.WriteLine("Usage: JRunner.FixtureBuilder <fixtures-directory>");
            return 2;
        }

        Generate(Path.GetFullPath(arguments[0]));
        return 0;
    }

    /// <summary>
    /// Writes a deterministic fixture tree into a missing directory.
    /// </summary>
    /// <remarks>
    /// The generator never replaces an existing directory, including an empty one. It creates a
    /// private sibling staging tree and atomically publishes it only while the requested output
    /// path remains absent. Generate into a fresh path and review its contents before replacing
    /// checked-in fixtures through normal source-control workflow.
    /// </remarks>
    public static void Generate(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string outputRoot = NormalizeOutputRoot(root);
        if (Directory.Exists(outputRoot))
        {
            throw new InvalidOperationException(
                "The fixture output directory must be missing; existing directories are never replaced.");
        }

        string parentDirectory = Path.GetDirectoryName(outputRoot)
            ?? throw new InvalidOperationException("The fixture output directory must have a parent directory.");
        string stagingRoot = CreateStagingRoot(parentDirectory);
        GenerateContents(stagingRoot);
        PublishStagingTree(stagingRoot, outputRoot);
    }

    private static void GenerateContents(string root)
    {
        var files = new List<FixtureFile>();
        CpuKey smallBlockCpuKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");

        WriteText(root, "keys/small-block.cpukey", "00112233445566778899AABBCCDDEEFF\n", files);

        byte[] logical = CreateInspectableLogicalNand(smallBlockCpuKey);
        try
        {
            byte[] smallBlock = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout0).ToArray();
            try
            {
                WriteBytes(root, "nand/small-block.bin", smallBlock, files);

                byte[] remapped = CreateRemappedSmallBlockImage(smallBlock);
                try
                {
                    WriteBytes(root, "nand/small-block-remapped-equivalent.bin", remapped, files);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(remapped);
                }

                WritePhysicalPattern(root, "nand/layout1-two-block.bin", NandPhysicalLayout.Layout1, 2, files);
                WritePhysicalPattern(root, "nand/layout2-two-block.bin", NandPhysicalLayout.Layout2, 2, files);

                byte[] completePatch = EncodeWords(LegacyPatchWords);
                try
                {
                    WriteBytes(root, "patches/patches.bin", completePatch, files);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(completePatch);
                }

                byte[] truncatedPatch = EncodeWords(0x0000C000U, 2U, 0x38800000U);
                try
                {
                    WriteBytes(root, "patches/truncated.bin", truncatedPatch, files);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(truncatedPatch);
                }

                WriteManifest(root, files, smallBlock.LongLength, logical.LongLength);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(smallBlock);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(logical);
        }
    }

    private static string NormalizeOutputRoot(string root)
    {
        string outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? parentDirectory = Path.GetDirectoryName(outputRoot);
        if (string.IsNullOrEmpty(parentDirectory) ||
            string.Equals(outputRoot, Path.GetPathRoot(outputRoot), StringComparison.Ordinal))
        {
            throw new ArgumentException("The fixture output root must be a descendant directory.", nameof(root));
        }

        EnsureNoLinkAncestors(outputRoot);
        return outputRoot;
    }

    private static string CreateStagingRoot(string parentDirectory)
    {
        EnsureNoLinkAncestors(parentDirectory);

        const int MaximumAttempts = 16;
        for (int attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            string stagingRoot = Path.Combine(parentDirectory, $".jrunner-fixture-builder-{Guid.NewGuid():N}");
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(stagingRoot);
                }
                else
                {
                    Directory.CreateDirectory(
                        stagingRoot,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch (IOException) when (attempt < MaximumAttempts - 1)
            {
                continue;
            }

            EnsureNoLinkAncestors(stagingRoot);
            if (!Directory.EnumerateFileSystemEntries(stagingRoot).Any())
            {
                return stagingRoot;
            }
        }

        throw new IOException("Unable to reserve a private staging directory for fixture generation.");
    }

    private static void PublishStagingTree(string stagingRoot, string outputRoot)
    {
        EnsureNoLinkAncestors(outputRoot);

        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (RenameAt2(
                    CurrentWorkingDirectoryFileDescriptor,
                    stagingRoot,
                    CurrentWorkingDirectoryFileDescriptor,
                    outputRoot,
                    RenameNoReplace) == 0)
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new PlatformNotSupportedException(
                    "Atomic fixture publication requires Linux renameat2 support.",
                    exception);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (RenameExclusivePath(stagingRoot, outputRoot, RenameExclusive) == 0)
            {
                return;
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            if (MoveFile(stagingRoot, outputRoot))
            {
                return;
            }
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Atomic fixture publication is supported only on Linux, macOS, and Windows.");
        }

        int error = Marshal.GetLastWin32Error();
        throw new InvalidOperationException(
            $"The fixture output path became unavailable (native error {error}); generated files remain at '{stagingRoot}'.");
    }

    private static void EnsureNoLinkAncestors(string path)
    {
        for (string? candidate = path; candidate is not null; candidate = Path.GetDirectoryName(candidate))
        {
            var directory = new DirectoryInfo(candidate);
            if (directory.LinkTarget is not null)
            {
                throw new InvalidOperationException("The fixture output path cannot contain symbolic links.");
            }

            if (directory.Exists)
            {
                continue;
            }

            var file = new FileInfo(candidate);
            if (file.Exists || file.LinkTarget is not null)
            {
                throw new InvalidOperationException("The fixture output path must not cross a file or symbolic link.");
            }
        }
    }

    private static void WritePhysicalPattern(
        string root,
        string relativePath,
        NandPhysicalLayout layout,
        int blockCount,
        List<FixtureFile> files)
    {
        var logical = new byte[checked(layout.Geometry.LogicalBlockSize * blockCount)];
        FillPattern(logical, multiplier: 31, increment: 7);
        byte[] physical = NandEccCodec.AddEcc(logical, layout).ToArray();
        try
        {
            WriteBytes(root, relativePath, physical, files);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(logical);
            CryptographicOperations.ZeroMemory(physical);
        }
    }

    private static byte[] CreateInspectableLogicalNand(CpuKey cpuKey)
    {
        var image = new byte[checked(SmallBlockCount * NandPhysicalLayout.Layout0.Geometry.LogicalBlockSize)];
        image.AsSpan().Fill(byte.MaxValue);
        image[0] = 0xFF;
        image[1] = 0x4F;
        WriteUInt32BigEndian(image, 0x08, FirstStageOffset);
        WriteUInt32BigEndian(image, 0x78, SmcLength);
        WriteUInt32BigEndian(image, 0x7C, SmcOffset);

        byte[] decryptedSmc = CreateDecryptedSmc();
        byte[] encryptedSmc = SmcCrypto.Encrypt(decryptedSmc);
        byte[] keyvault = CreateDecryptedKeyvault();
        byte[] encryptedKeyvault = EncryptKeyvault(keyvault, cpuKey);
        byte[] cbA = CreateEncryptedCbA(9188);
        try
        {
            encryptedSmc.CopyTo(image, SmcOffset);
            encryptedKeyvault.CopyTo(image, KeyvaultOffset);
            cbA.CopyTo(image, FirstStageOffset);
            WriteWords(image.AsSpan(PrimaryPatchOffset), LegacyPatchWords);
            return image;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(image);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decryptedSmc);
            CryptographicOperations.ZeroMemory(encryptedSmc);
            CryptographicOperations.ZeroMemory(keyvault);
            CryptographicOperations.ZeroMemory(encryptedKeyvault);
            CryptographicOperations.ZeroMemory(cbA);
        }
    }

    private static byte[] CreateDecryptedSmc()
    {
        var decryptedSmc = new byte[SmcLength];
        decryptedSmc[0x100] = 0x60;
        decryptedSmc[0x101] = 1;
        decryptedSmc[0x102] = 2;
        return decryptedSmc;
    }

    private static byte[] CreateDecryptedKeyvault()
    {
        var keyvault = new byte[KeyvaultService.KeyvaultLength];
        Encoding.ASCII.GetBytes("TESTSERIAL01").CopyTo(keyvault, 0xB0);
        Encoding.ASCII.GetBytes("SYNTHETIC-DVD-KEY").CopyTo(keyvault, 0x100);
        Encoding.ASCII.GetBytes("SYNTHETIC-DRIVE-INQUIRY-0000").CopyTo(keyvault, 0xC92);
        keyvault[0x9CA] = 0x10;
        keyvault[0x9CB] = 0x20;
        keyvault[0x9CC] = 0x30;
        keyvault[0x9CD] = 0x40;
        keyvault[0x9CE] = 0x50;
        return keyvault;
    }

    private static byte[] EncryptKeyvault(ReadOnlySpan<byte> decrypted, CpuKey cpuKey)
    {
        byte[] encrypted = decrypted.ToArray();
        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            cpuKey.CopyTo(keyBytes);
            XeCrypt.HmacSha1Truncated(keyBytes, encrypted.AsSpan(0, 0x10), rc4Key);
            Rc4.TransformInPlace(rc4Key, encrypted.AsSpan(0x10));
            return encrypted;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(encrypted);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static byte[] CreateEncryptedCbA(int build)
    {
        var stage = new byte[CbStageLength];
        WriteBootloaderHeader(stage, "CB", build, stage.Length);
        for (int index = 0; index < 0x10; index++)
        {
            stage[0x10 + index] = checked((byte)(0x20 + index));
        }

        stage[0x20] = 0x11;
        stage[0x21] = 0x22;
        stage[0x22] = 0x33;
        stage[0x3B1] = 9;
        byte[] firstBootLoaderKey =
        [
            0xDD, 0x88, 0xAD, 0x0C, 0x9E, 0xD6, 0x69, 0xE7,
            0xB5, 0x67, 0x94, 0xFB, 0x68, 0x56, 0x3E, 0xFA,
        ];
        byte[] rc4Key = XeCrypt.HmacSha1Truncated(firstBootLoaderKey, stage.AsSpan(0x10, 0x10));
        try
        {
            Rc4.TransformInPlace(rc4Key, stage.AsSpan(0x20));
            return stage;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(stage);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstBootLoaderKey);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static byte[] CreateRemappedSmallBlockImage(ReadOnlySpan<byte> smallBlock)
    {
        NandPhysicalLayout layout = NandPhysicalLayout.Layout0;
        int physicalBlockSize = layout.Geometry.PhysicalBlockSize;
        var remapped = smallBlock.ToArray();
        smallBlock.Slice(0, physicalBlockSize).CopyTo(remapped.AsSpan(
            checked((SmallBlockCount - 1) * physicalBlockSize),
            physicalBlockSize));
        remapped[layout.MarkerOffset] = 0;
        return remapped;
    }

    private static byte[] EncodeWords(params uint[] words)
    {
        var bytes = new byte[checked(words.Length * sizeof(uint))];
        WriteWords(bytes, words);
        return bytes;
    }

    private static void WriteWords(Span<byte> destination, ReadOnlySpan<uint> words)
    {
        if (destination.Length < checked(words.Length * sizeof(uint)))
        {
            throw new ArgumentException("The destination does not have room for every patch word.", nameof(destination));
        }

        for (int index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(index * sizeof(uint), sizeof(uint)), words[index]);
        }
    }

    private static void FillPattern(Span<byte> bytes, int multiplier, int increment)
    {
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = unchecked((byte)((index * multiplier) + increment));
        }
    }

    private static void WriteBootloaderHeader(byte[] destination, string magic, int build, int declaredLength)
    {
        destination[0] = checked((byte)magic[0]);
        destination[1] = checked((byte)magic[1]);
        BinaryPrimitives.WriteUInt16BigEndian(destination.AsSpan(2, sizeof(ushort)), checked((ushort)build));
        WriteUInt32BigEndian(destination, 0x0C, declaredLength);
    }

    private static void WriteUInt32BigEndian(byte[] destination, int offset, int value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset, sizeof(uint)), checked((uint)value));
    }

    private static void WriteBytes(string root, string relativePath, ReadOnlySpan<byte> content, List<FixtureFile> files)
    {
        string path = ResolvePath(root, relativePath);
        string? directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new InvalidOperationException("Fixture paths must have a parent directory.");
        }

        EnsureNoLinkAncestors(directory);
        Directory.CreateDirectory(directory);
        EnsureNoLinkAncestors(directory);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
        }
        files.Add(CreateFileEntry(relativePath, path));
    }

    private static void WriteText(string root, string relativePath, string content, List<FixtureFile> files)
    {
        string path = ResolvePath(root, relativePath);
        string? directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new InvalidOperationException("Fixture paths must have a parent directory.");
        }

        EnsureNoLinkAncestors(directory);
        Directory.CreateDirectory(directory);
        EnsureNoLinkAncestors(directory);
        WriteUtf8Text(path, content);
        files.Add(CreateFileEntry(relativePath, path));
    }

    private static void WriteUtf8Text(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static FixtureFile CreateFileEntry(string relativePath, string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] digest = SHA256.HashData(stream);
        return new FixtureFile(relativePath, new FileInfo(path).Length, Convert.ToHexString(digest));
    }

    private static void WriteManifest(string root, IReadOnlyList<FixtureFile> files, long smallBlockRawLength, long smallBlockLogicalLength)
    {
        var manifest = new FixtureManifest(
            SchemaVersion: 1,
            FixtureSet: FixtureSet,
            Files: files,
            Scenarios: new FixtureScenarios(
                NandInspection: new NandInspectionScenario(
                    Input: "nand/small-block.bin",
                    CpuKey: "keys/small-block.cpukey",
                    Format: "interleaved-ecc",
                    Layout: "layout0",
                    RawByteLength: smallBlockRawLength,
                    CanonicalLogicalByteLength: smallBlockLogicalLength,
                    CbBuild: 9188,
                    SmcVersion: "1.02",
                    CpuKeyVerification: "verified",
                    LegacyPatches: ["FuseBlow", "XLUSB", "XLHDD", "UsbdSec", "CoronaKeyFix"]),
                CanonicalComparison: new CanonicalComparisonScenario(
                    Left: "nand/small-block.bin",
                    Right: "nand/small-block-remapped-equivalent.bin",
                    Layout: "layout0",
                    BadPhysicalBlock: 0,
                    ReplacementPhysicalBlock: SmallBlockCount - 1),
                PatchInspection: new PatchInspectionScenario(
                    CompleteInput: "patches/patches.bin",
                    MalformedInput: "patches/truncated.bin",
                    RecordCount: 5,
                    LegacyPatches: ["FuseBlow", "XLUSB", "XLHDD", "UsbdSec", "CoronaKeyFix"],
                    MalformedDiagnosticKind: "truncated-payload"),
                PhysicalFormats:
                [
                    new PhysicalFormatScenario("nand/small-block.bin", "layout0"),
                    new PhysicalFormatScenario("nand/layout1-two-block.bin", "layout1"),
                    new PhysicalFormatScenario("nand/layout2-two-block.bin", "layout2"),
                ]));
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        string output = JsonSerializer.Serialize(manifest, options) + "\n";
        string manifestPath = ResolvePath(root, "manifest.v1.json");
        EnsureNoLinkAncestors(root);
        WriteUtf8Text(manifestPath, output);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Fixture paths must be nonempty relative paths.");
        }

        string[] segments = relativePath.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidOperationException("Fixture paths must be normalized descendant paths.");
        }

        return string.Join("/", segments);
    }

    private static string ResolvePath(string root, string relativePath)
    {
        string normalizedRoot = Path.GetFullPath(root);
        string normalizedRelativePath = NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(normalizedRoot, normalizedRelativePath));
        string rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fixture paths must stay under the selected output directory.");
        }

        return path;
    }

    private sealed record FixtureManifest(
        int SchemaVersion,
        string FixtureSet,
        IReadOnlyList<FixtureFile> Files,
        FixtureScenarios Scenarios);

    private sealed record FixtureFile(string Path, long ByteLength, string Sha256);

    private sealed record FixtureScenarios(
        NandInspectionScenario NandInspection,
        CanonicalComparisonScenario CanonicalComparison,
        PatchInspectionScenario PatchInspection,
        IReadOnlyList<PhysicalFormatScenario> PhysicalFormats);

    private sealed record NandInspectionScenario(
        string Input,
        string CpuKey,
        string Format,
        string Layout,
        long RawByteLength,
        long CanonicalLogicalByteLength,
        int CbBuild,
        string SmcVersion,
        string CpuKeyVerification,
        IReadOnlyList<string> LegacyPatches);

    private sealed record CanonicalComparisonScenario(
        string Left,
        string Right,
        string Layout,
        long BadPhysicalBlock,
        long ReplacementPhysicalBlock);

    private sealed record PatchInspectionScenario(
        string CompleteInput,
        string MalformedInput,
        int RecordCount,
        IReadOnlyList<string> LegacyPatches,
        string MalformedDiagnosticKind);

    private sealed record PhysicalFormatScenario(string Input, string Layout);
}
