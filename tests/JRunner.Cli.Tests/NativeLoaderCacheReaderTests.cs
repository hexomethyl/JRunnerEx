using System.Buffers.Binary;
using System.Text;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class NativeLoaderCacheReaderTests
{
    [Fact]
    public void Current_cache_retains_every_duplicate_architecture_and_hwcaps_candidate()
    {
        byte[] bytes = CreateCache(
            [("libexample.so.1", "/protected/base/libexample.so.1", 0x303, 0),
             ("libexample.so.1", "/protected/glibc-hwcaps/x86-64-v3/libexample.so.1", 0x303, (1UL << 62)),
             ("libexample.so.1", "/protected/i386/libexample.so.1", 3, 0)],
            ["x86-64-v3"]);

        NativeLoaderCacheImage image = NativeLoaderCacheReader.Read(new MemoryStream(bytes));

        Assert.Equal(3, image.Entries.Count);
        Assert.Equal("/protected/base/libexample.so.1", image.Entries[0].Path);
        Assert.Equal("/protected/glibc-hwcaps/x86-64-v3/libexample.so.1", image.Entries[1].Path);
        Assert.Equal(3, image.Entries[2].Flags);
        Assert.Equal(["x86-64-v3"], image.HardwareCapabilityNames);
    }

    [Fact]
    public void Validated_compat_prefix_is_not_a_second_uninspected_cache()
    {
        byte[] bytes = CreateCache([("libexample.so.1", "/protected/libexample.so.1", 0x303, 0)], compat: true);

        NativeLoaderCacheImage image = NativeLoaderCacheReader.Read(new MemoryStream(bytes));

        Assert.Equal(3, image.Entries.Count); // new entry plus the two compat entries.
        Assert.All(image.Entries, entry => Assert.Equal("/protected/libexample.so.1", entry.Path));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), uint.MaxValue);
        AssertUnavailable(() => NativeLoaderCacheReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void Ubuntu_Jammy_Linux_ABI_versions_and_legacy_x86_capabilities_are_candidate_filters_not_discovery()
    {
        const ulong capabilities = 7UL | (15UL << 48) | (1UL << 63);
        byte[] bytes = CreateCache([("libexample.so.1", "/protected/tls/haswell/libexample.so.1", 0x303, capabilities)]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), 0x00030200);

        NativeLoaderCacheEntry entry = Assert.Single(NativeLoaderCacheReader.Read(new MemoryStream(bytes)).Entries);

        Assert.Equal(capabilities, entry.HardwareCapabilities);
        Assert.Equal("/protected/tls/haswell/libexample.so.1", entry.Path);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("big-endian")]
    [InlineData("invalid-endian")]
    [InlineData("unknown-flags")]
    [InlineData("padding")]
    [InlineData("count-overflow")]
    [InlineData("strings-overflow")]
    [InlineData("key-in-header")]
    [InlineData("key-outside-file")]
    [InlineData("value-in-header")]
    [InlineData("unterminated")]
    [InlineData("invalid-utf8")]
    [InlineData("unknown-architecture")]
    [InlineData("unknown-osversion")]
    [InlineData("unknown-hwcap")]
    [InlineData("hwcap-without-section")]
    [InlineData("truncated")]
    [InlineData("trailing-data")]
    public void Unknown_or_malformed_cache_metadata_is_redacted(string mutation)
    {
        byte[] bytes = CreateCache([("libexample.so.1", "/protected/libexample.so.1", 0x303, 0)]);
        switch (mutation)
        {
            case "magic": bytes[0] ^= 0x20; break;
            case "version": bytes[19] = (byte)'2'; break;
            case "big-endian": bytes[28] = 3; break;
            case "invalid-endian": bytes[28] = 1; break;
            case "unknown-flags": bytes[28] = 6; break;
            case "padding": bytes[29] = 1; break;
            case "count-overflow": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), uint.MaxValue); break;
            case "strings-overflow": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), uint.MaxValue); break;
            case "key-in-header": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), 1); break;
            case "key-outside-file": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), uint.MaxValue); break;
            case "value-in-header": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), 1); break;
            case "unterminated": bytes[^1] = (byte)'X'; break;
            case "invalid-utf8": bytes[72] = 0xFF; break;
            case "unknown-architecture": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48), 0xA03); break;
            case "unknown-osversion": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), 0x01000000); break;
            case "unknown-hwcap": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64), 1UL << 10); break;
            case "hwcap-without-section": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64), 1UL << 62); break;
            case "truncated": bytes = bytes[..^1]; break;
            case "trailing-data": bytes = [.. bytes, 0x42]; break;
        }

        AssertUnavailable(() => NativeLoaderCacheReader.Read(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData("extension-magic")]
    [InlineData("extension-alignment")]
    [InlineData("unknown-tag")]
    [InlineData("duplicate-tag")]
    [InlineData("section-flags")]
    [InlineData("section-offset")]
    [InlineData("section-overlap")]
    [InlineData("section-size")]
    [InlineData("hwcap-index")]
    [InlineData("hwcap-string-offset")]
    [InlineData("hwcap-name")]
    public void Every_extension_and_hwcaps_offset_is_checked(string mutation)
    {
        byte[] bytes = CreateCache(
            [("libexample.so.1", "/protected/libexample.so.1", 0x303, 1UL << 62)], ["x86-64-v3"]);
        int extension = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)));
        int hwcaps = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(extension + 32)));
        switch (mutation)
        {
            case "extension-magic": bytes[extension] ^= 1; break;
            case "extension-alignment": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), (uint)extension + 1); break;
            case "unknown-tag": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 8), 2); break;
            case "duplicate-tag": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 24), 0); break;
            case "section-flags": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 12), 1); break;
            case "section-offset": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 16), uint.MaxValue); break;
            case "section-overlap": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 16), (uint)hwcaps); break;
            case "section-size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 36), 3); break;
            case "hwcap-index": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64), (1UL << 62) | 1); break;
            case "hwcap-string-offset": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(hwcaps), uint.MaxValue); break;
            case "hwcap-name":
                int name = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(hwcaps)));
                bytes[name] = (byte)'/';
                break;
        }

        AssertUnavailable(() => NativeLoaderCacheReader.Read(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData("relative/libexample.so.1")]
    [InlineData("/protected/../attacker/libexample.so.1")]
    [InlineData("/protected//libexample.so.1")]
    [InlineData("/protected/$LIB/libexample.so.1")]
    [InlineData("/protected/libexample.so.1\nsecret")]
    public void Cache_paths_cannot_add_relative_token_or_traversal_search_state(string path)
    {
        byte[] bytes = CreateCache([("libexample.so.1", path, 0x303, 0)]);

        AssertUnavailable(() => NativeLoaderCacheReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void Reader_uses_bounded_reads_and_never_requires_a_whole_cache_allocation()
    {
        byte[] bytes = CreateCache([("libexample.so.1", "/protected/libexample.so.1", 0x303, 0)]);
        using var stream = new ShortReadStream(bytes);

        Assert.Single(NativeLoaderCacheReader.Read(stream).Entries);
        Assert.True(stream.LargestRequestedRead <= 4096);
    }

    internal static byte[] CreateCache(
        (string Name, string Path, int Flags, ulong HardwareCapabilities)[] entries,
        string[]? hwcaps = null,
        bool compat = false)
    {
        hwcaps ??= [];
        int oldCount = compat ? (entries.Length + 1) & ~1 : 0;
        int prefix = compat ? 16 + oldCount * 12 : 0;
        int tableStart = prefix + 48 + entries.Length * 24;
        var strings = new List<byte>();
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint AddString(string value)
        {
            if (!offsets.TryGetValue(value, out uint offset))
            {
                offset = checked((uint)(tableStart - prefix + strings.Count));
                offsets.Add(value, offset);
                strings.AddRange(Encoding.UTF8.GetBytes(value));
                strings.Add(0);
            }
            return offset;
        }
        foreach (var entry in entries)
        {
            AddString(entry.Name);
            AddString(entry.Path);
        }
        uint[] hwcapOffsets = hwcaps.Select(AddString).ToArray();
        byte[] generator = Encoding.UTF8.GetBytes("ldconfig test release version 2.39");
        int extension = hwcaps.Length == 0 ? 0 : (tableStart + strings.Count + 3) & ~3;
        int hwcapData = extension + 40;
        int generatorData = hwcapData + hwcaps.Length * 4;
        byte[] bytes = new byte[extension == 0 ? tableStart + strings.Count : generatorData + generator.Length];
        if (compat)
        {
            "ld.so-1.7.0"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)oldCount);
            for (int index = 0; index < oldCount; index++)
            {
                var entry = entries[Math.Min(index, entries.Length - 1)];
                int position = 16 + index * 12;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(position), entry.Flags);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 4), offsets[entry.Name]);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 8), offsets[entry.Path]);
            }
        }
        "glibc-ld.so.cache1.1"u8.CopyTo(bytes.AsSpan(prefix));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(prefix + 20), (uint)entries.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(prefix + 24), (uint)strings.Count);
        bytes[prefix + 28] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(prefix + 32), (uint)extension);
        for (int index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            int position = prefix + 48 + index * 24;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(position), entry.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 4), offsets[entry.Name]);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 8), offsets[entry.Path]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(position + 16), entry.HardwareCapabilities);
        }
        strings.CopyTo(bytes, tableStart);
        if (extension != 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension), unchecked((uint)-358342284));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 8), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 16), (uint)generatorData);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 20), (uint)generator.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 24), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 32), (uint)hwcapData);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 36), (uint)hwcaps.Length * 4);
            for (int index = 0; index < hwcaps.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(hwcapData + index * 4), hwcapOffsets[index]);
            }
            generator.CopyTo(bytes, generatorData);
        }
        return bytes;
    }

    private static void AssertUnavailable(Action action)
    {
        OperationFailureException failure = Assert.Throws<OperationFailureException>(action);
        Assert.Equal(ExitCode.MissingPrerequisite, failure.Failure.Code);
        Assert.Equal("native-closure-unavailable", failure.Failure.Kind);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("/protected", failure.Message, StringComparison.Ordinal);
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int LargestRequestedRead { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            LargestRequestedRead = Math.Max(LargestRequestedRead, buffer.Length);
            return base.Read(buffer[..Math.Min(buffer.Length, 7)]);
        }
    }
}
