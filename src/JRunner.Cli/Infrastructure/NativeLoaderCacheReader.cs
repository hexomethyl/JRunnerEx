using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace JRunner.Cli.Infrastructure;

internal sealed record NativeLoaderCacheEntry(string Name, string Path, int Flags, ulong HardwareCapabilities);

internal sealed record NativeLoaderCacheImage(
    IReadOnlyList<NativeLoaderCacheEntry> Entries,
    IReadOnlyList<string> HardwareCapabilityNames);

/// <summary>Reads ldconfig's on-disk data, without invoking either ldconfig or the loader.</summary>
internal static class NativeLoaderCacheReader
{
    internal const int MaximumCacheBytes = 32 * 1024 * 1024;
    internal const int MaximumEntries = 131072;
    private const int MaximumStringBytes = 4096;
    private const int MaximumStringTableBytes = 16 * 1024 * 1024;
    private const uint ExtensionMagic = unchecked((uint)-358342284);
    private const ulong HardwareCapabilityExtension = 1UL << 62;
    private const ulong LegacyX86HardwareCapabilities = 7UL | (15UL << 48) | (1UL << 63);
    private static readonly UTF8Encoding Encoding = new(false, true);

    // glibc-2.39 sysdeps/generic/dl-cache.h: cache_file_new is 48 bytes,
    // file_entry_new is 24 bytes, string indices are relative to the NEW header,
    // but extension offsets are relative to the FILE. elf/cache.c:save_cache and
    // write_extensions specify the compat prefix and extension/string layout.
    // https://github.com/bminor/glibc/blob/glibc-2.39/sysdeps/generic/dl-cache.h
    // https://github.com/bminor/glibc/blob/glibc-2.39/elf/cache.c
    // This policy accepts little-endian 1.1 (including its documented unset
    // endian flag), and a validated compat prefix containing that same format.
    // It deliberately does not interpret old-only or unknown future formats.
    internal static NativeLoaderCacheImage Read(Stream stream)
    {
        try
        {
            if (stream is null || !stream.CanRead || !stream.CanSeek ||
                stream.Length < 48 || stream.Length > MaximumCacheBytes)
            {
                throw NativeElfReader.Failure();
            }
            return new Reader(stream).Read();
        }
        catch (Exception)
        {
            throw NativeElfReader.Failure();
        }
    }

    private sealed class Reader(Stream stream)
    {
        private readonly Dictionary<long, string> _strings = [];
        private long _stringsStart;
        private long _stringsEnd;
        private long _decodedBytes;

        internal NativeLoaderCacheImage Read()
        {
            Span<byte> header = stackalloc byte[48];
            ReadAt(0, header);
            long newHeader = 0;
            uint oldCount = 0;
            long oldStringsBase = 0;
            if (!header[..20].SequenceEqual("glibc-ld.so.cache1.1"u8))
            {
                if (!header[..11].SequenceEqual("ld.so-1.7.0"u8) || header[11] != 0)
                {
                    throw NativeElfReader.Failure();
                }
                oldCount = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
                if (oldCount > MaximumEntries || (oldCount & 1) != 0)
                {
                    throw NativeElfReader.Failure();
                }
                oldStringsBase = checked(16L + oldCount * 12L);
                newHeader = checked((oldStringsBase + 7) & ~7L);
                RequireZero(oldStringsBase, newHeader);
                ReadAt(newHeader, header);
                if (!header[..20].SequenceEqual("glibc-ld.so.cache1.1"u8))
                {
                    throw NativeElfReader.Failure();
                }
            }

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            uint stringBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            // dl-cache.h cache_file_new_matches_endian accepts 0 or native endian.
            // Unused flag bits/padding are stricter here: unknown semantics fail.
            if (count > MaximumEntries || stringBytes > MaximumStringTableBytes ||
                header[28] is not (0 or 2) || !IsZero(header[29..32]) || !IsZero(header[36..48]))
            {
                throw NativeElfReader.Failure();
            }
            _stringsStart = checked(newHeader + 48 + count * 24L);
            _stringsEnd = checked(_stringsStart + stringBytes);
            if (_stringsEnd > stream.Length || (count != 0 && stringBytes == 0))
            {
                throw NativeElfReader.Failure();
            }

            uint extensionOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
            string[] hardwareNames = ReadExtensions(extensionOffset, newHeader);
            var entries = new List<NativeLoaderCacheEntry>(checked((int)(count + oldCount)));
            Span<byte> entry = stackalloc byte[24];
            for (uint index = 0; index < count; index++)
            {
                ReadAt(newHeader + 48 + index * 24L, entry);
                int flags = BinaryPrimitives.ReadInt32LittleEndian(entry);
                ValidateFlags(flags);
                // glibc <= 2.36 recorded the GNU ABI-note OS/version here.
                // Linux is OS byte zero; its three version bytes only filter
                // candidates. We overapproximate them instead of querying the
                // running kernel. Current glibc writes the unused field zero.
                // https://github.com/bminor/glibc/blob/glibc-2.35/elf/readelflib.c
                if ((BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]) >> 24) != 0)
                {
                    throw NativeElfReader.Failure();
                }
                ulong hwcap = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
                if ((hwcap & HardwareCapabilityExtension) != 0)
                {
                    // dl_cache_hwcap_extension: bit 62 plus ten ISA bits, with
                    // the low uint32 indexing the glibc-hwcaps section.
                    if (((hwcap >> 32) & ~0x3FFUL) != (HardwareCapabilityExtension >> 32) ||
                        (uint)hwcap >= hardwareNames.Length)
                    {
                        throw NativeElfReader.Failure();
                    }
                }
                else if ((hwcap & ~LegacyX86HardwareCapabilities) != 0)
                {
                    throw NativeElfReader.Failure();
                }
                // Older x86 caches encode sse2/x86_64/avx512_1 in bits0..2,
                // four platforms in48..51 and tls in63. These only filter
                // this SAME absolute candidate; every candidate is closed.
                // https://github.com/bminor/glibc/blob/glibc-2.35/sysdeps/x86/dl-hwcap.h
                // https://github.com/bminor/glibc/blob/glibc-2.35/sysdeps/x86/dl-procinfo.h
                // https://github.com/bminor/glibc/blob/glibc-2.35/elf/ldconfig.c
                entries.Add(ReadEntry(entry, newHeader, flags, hwcap));
            }

            // Current loaders prefer the valid new table. Validate and include
            // the compat entries too; never let a different alias hide there.
            for (uint index = 0; index < oldCount; index++)
            {
                ReadAt(16 + index * 12L, entry[..12]);
                int flags = BinaryPrimitives.ReadInt32LittleEndian(entry);
                ValidateFlags(flags);
                entries.Add(ReadEntry(entry, oldStringsBase, flags, 0));
            }
            return new NativeLoaderCacheImage(
                new ReadOnlyCollection<NativeLoaderCacheEntry>(entries),
                Array.AsReadOnly(hardwareNames));
        }

        private NativeLoaderCacheEntry ReadEntry(ReadOnlySpan<byte> entry, long stringBase, int flags, ulong hwcap)
        {
            string name = ReadString(checked(stringBase + BinaryPrimitives.ReadUInt32LittleEndian(entry[4..])));
            string path = ReadString(checked(stringBase + BinaryPrimitives.ReadUInt32LittleEndian(entry[8..])));
            if (!IsName(name) || !IsAbsoluteLiteralPath(path))
            {
                throw NativeElfReader.Failure();
            }
            return new NativeLoaderCacheEntry(name, path, flags, hwcap);
        }

        private string[] ReadExtensions(uint offset, long newHeader)
        {
            if (offset == 0)
            {
                if (_stringsEnd != stream.Length)
                {
                    throw NativeElfReader.Failure();
                }
                return [];
            }
            if ((offset & 3) != 0 || offset < _stringsEnd || offset - _stringsEnd > 3)
            {
                throw NativeElfReader.Failure();
            }
            RequireZero(_stringsEnd, offset);
            Span<byte> extension = stackalloc byte[8];
            ReadAt(offset, extension);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(extension[4..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(extension) != ExtensionMagic || count is < 1 or > 2)
            {
                throw NativeElfReader.Failure();
            }
            long directoryEnd = checked(offset + 8 + count * 16L);
            if (directoryEnd > stream.Length)
            {
                throw NativeElfReader.Failure();
            }
            var ranges = new List<(long Start, long End)>();
            var seenTags = new HashSet<uint>();
            string[] names = [];
            Span<byte> section = stackalloc byte[16];
            Span<byte> generator = stackalloc byte[MaximumStringBytes];
            Span<byte> word = stackalloc byte[4];
            for (uint index = 0; index < count; index++)
            {
                ReadAt(offset + 8 + index * 16L, section);
                uint tag = BinaryPrimitives.ReadUInt32LittleEndian(section);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(section[4..]);
                uint start = BinaryPrimitives.ReadUInt32LittleEndian(section[8..]);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(section[12..]);
                long end = checked((long)start + size);
                if (tag > 1 || !seenTags.Add(tag) || flags != 0 || size == 0 ||
                    start < directoryEnd || end > stream.Length)
                {
                    throw NativeElfReader.Failure();
                }
                ranges.Add((start, end));
                if (tag == 0)
                {
                    if (size > MaximumStringBytes)
                    {
                        throw NativeElfReader.Failure();
                    }
                    ReadAt(start, generator[..(int)size]);
                    string generatorName = Encoding.GetString(generator[..(int)size]);
                    if (generatorName.Length == 0 || generatorName.Any(char.IsControl))
                    {
                        throw NativeElfReader.Failure();
                    }
                }
                else
                {
                    if ((start & 3) != 0 || (size & 3) != 0 || size / 4 > 4096)
                    {
                        throw NativeElfReader.Failure();
                    }
                    names = new string[size / 4];
                    for (int nameIndex = 0; nameIndex < names.Length; nameIndex++)
                    {
                        ReadAt(start + nameIndex * 4L, word);
                        names[nameIndex] = ReadString(checked(newHeader + BinaryPrimitives.ReadUInt32LittleEndian(word)));
                        if (!IsName(names[nameIndex]) || names[nameIndex].Contains(':') ||
                            (nameIndex != 0 && string.CompareOrdinal(names[nameIndex - 1], names[nameIndex]) >= 0))
                        {
                            throw NativeElfReader.Failure();
                        }
                    }
                }
            }
            ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            long position = directoryEnd;
            foreach ((long start, long end) in ranges)
            {
                // write_extensions writes contiguous section payloads. Unknown
                // trailing or overlapping data is not a supported extension.
                if (start != position)
                {
                    throw NativeElfReader.Failure();
                }
                position = end;
            }
            if (position != stream.Length)
            {
                throw NativeElfReader.Failure();
            }
            return names;
        }

        private string ReadString(long offset)
        {
            if (offset < _stringsStart || offset >= _stringsEnd)
            {
                throw NativeElfReader.Failure();
            }
            if (_strings.TryGetValue(offset, out string? existing))
            {
                return existing;
            }
            Span<byte> buffer = stackalloc byte[MaximumStringBytes];
            int count = (int)Math.Min(buffer.Length, _stringsEnd - offset);
            ReadAt(offset, buffer[..count]);
            int terminator = buffer[..count].IndexOf((byte)0);
            if (terminator <= 0 || (_decodedBytes += terminator) > MaximumStringTableBytes)
            {
                throw NativeElfReader.Failure();
            }
            string value = Encoding.GetString(buffer[..terminator]);
            if (value.Any(char.IsControl))
            {
                throw NativeElfReader.Failure();
            }
            _strings.Add(offset, value);
            return value;
        }

        private void ReadAt(long offset, Span<byte> buffer)
        {
            if (offset < 0 || offset > stream.Length - buffer.Length)
            {
                throw NativeElfReader.Failure();
            }
            stream.Position = offset;
            stream.ReadExactly(buffer);
        }

        private void RequireZero(long start, long end)
        {
            if (end < start || end - start > 7)
            {
                throw NativeElfReader.Failure();
            }
            Span<byte> bytes = stackalloc byte[8];
            int count = checked((int)(end - start));
            ReadAt(start, bytes[..count]);
            if (!IsZero(bytes[..count]))
            {
                throw NativeElfReader.Failure();
            }
        }
    }

    // glibc-2.39 sysdeps/generic/ldconfig.h and sysdeps/x86_64/dl-cache.h:
    // i386 accepts ELF/ELF_LIBC6; x86-64 requires 0x303. Validate x32's
    // documented 0x803 even though it is not a supported execution ABI here.
    private static void ValidateFlags(int flags)
    {
        if (flags is not (1 or 3 or 0x303 or 0x803))
        {
            throw NativeElfReader.Failure();
        }
    }

    private static bool IsZero(ReadOnlySpan<byte> value)
    {
        foreach (byte item in value)
        {
            if (item != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsName(string value)
    {
        if (value.Length is <= 0 or > MaximumStringBytes || value is "." or ".." ||
            value.Contains('/') || value.Contains('$') || value.Contains(':'))
        {
            return false;
        }
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAbsoluteLiteralPath(string value)
    {
        if (!value.StartsWith('/') || value.Length > MaximumStringBytes || value.Contains('$') || value.Contains(':'))
        {
            return false;
        }
        ReadOnlySpan<char> remaining = value.AsSpan(1);
        while (true)
        {
            int slash = remaining.IndexOf('/');
            ReadOnlySpan<char> component = slash < 0 ? remaining : remaining[..slash];
            if (component.IsEmpty || component.SequenceEqual(".") || component.SequenceEqual(".."))
            {
                return false;
            }
            if (slash < 0)
            {
                return true;
            }
            remaining = remaining[(slash + 1)..];
        }
    }
}
