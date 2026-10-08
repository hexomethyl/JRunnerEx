using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

internal sealed record NativeElfImage(
    ushort ObjectType,
    ushort Machine,
    byte ElfClass,
    byte DataEncoding,
    byte OsAbi,
    string? Interpreter,
    IReadOnlyList<string> Needed,
    IReadOnlyList<string> RPath,
    IReadOnlyList<string> RunPath,
    string? Soname,
    ulong Flags,
    ulong Flags1)
{
    internal bool HasDynamicSegment { get; init; }
    internal bool HasSupportedAbi { get; init; }
    internal byte AbiVersion { get; init; }
}

// This reads loader metadata, never instructions, and never invokes the inspected loader.
// The owning closure protects/freezes native bytes; this parser never invokes the inspected loader.
internal static class NativeElfReader
{
    internal const int MaximumProgramHeaders = 1024;
    internal const int MaximumSectionHeaders = 8192;
    internal const int MaximumDynamicEntries = 4096;
    internal const int MaximumTableEntries = 1_048_576;
    internal const int MaximumStringBytes = 65_536;
    private const ulong MaximumStringTableBytes = 32 * 1024 * 1024;
    private const int MaximumExtractedStringBytes = 4 * 1024 * 1024;
    private const ulong SupportedFlags = 0x1f; // ORIGIN, SYMBOLIC, TEXTREL, BIND_NOW, STATIC_TLS.
    private const ulong SupportedFlags1 = 0x080008e9; // NOW, NODELETE, INITFIRST, NOOPEN, ORIGIN, NODEFLIB, PIE.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static OperationFailureException Failure() => new(
        ExitCode.MissingPrerequisite,
        "native-closure-unavailable",
        "Native executable dependencies cannot be safely determined from protected ELF metadata.");

    internal static NativeElfImage? Read(Stream stream) => ReadCore(stream, allowForeignMetadata: false);

    // Foreign module-root data is inspected, but must never satisfy a native library or executable binding.
    internal static NativeElfImage? ReadModuleMetadata(Stream stream) => ReadCore(stream, allowForeignMetadata: true);

    private static NativeElfImage? ReadCore(Stream stream, bool allowForeignMetadata)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            if (!stream.CanRead || !stream.CanSeek)
            {
                throw Failure();
            }
            long signedLength = stream.Length;
            Require(signedLength >= 0);
            stream.Position = 0;
            ulong length = (ulong)signedLength;
            Span<byte> magic = stackalloc byte[4];
            if (length < 4)
            {
                return null;
            }
            ReadAt(stream, length, 0, magic);
            if (!magic.SequenceEqual("\u007fELF"u8))
            {
                return null;
            }
            return new Parser(stream, length, allowForeignMetadata).ReadImage();
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or
            ArgumentException or OverflowException or ObjectDisposedException)
        {
            // Do not expose paths, dependency bytes, or a stream exception's message.
            throw Failure();
        }
    }

    private static void ReadAt(Stream stream, ulong length, ulong offset, Span<byte> destination)
    {
        Require(offset <= length && (ulong)destination.Length <= length - offset);
        long position = checked((long)offset);
        if (stream.Position != position)
        {
            stream.Position = position;
        }
        int consumed = 0;
        while (consumed < destination.Length)
        {
            int count = stream.Read(destination[consumed..]);
            Require(count > 0 && count <= destination.Length - consumed);
            consumed += count;
        }
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw Failure();
        }
    }

    private static ulong Add(ulong left, ulong right)
    {
        Require(right <= ulong.MaxValue - left);
        return left + right;
    }

    private static bool PowerOfTwo(ulong value) => value != 0 && (value & (value - 1)) == 0;

    private readonly record struct Segment(uint Type, uint Flags, ulong Offset, ulong Address, ulong FileSize, ulong MemorySize);

    private sealed class Parser(Stream stream, ulong length, bool allowForeignMetadata)
    {
        private readonly List<Segment> loads = [];
        private readonly Dictionary<ulong, ulong> tags = [];
        private readonly List<ulong> neededOffsets = [];
        private readonly Dictionary<ulong, string> strings = [];
        private byte elfClass;
        private ushort machine;
        private ulong stringOffset;
        private ulong stringSize;
        private byte[]? stringBuffer;
        private int extractedStringBytes;
        private int WordSize => elfClass == 1 ? 4 : 8;
        private int SymbolSize => elfClass == 1 ? 16 : 24;

        internal NativeElfImage ReadImage()
        {
            Span<byte> header = stackalloc byte[64];
            ReadAt(stream, length, 0, header[..16]);
            Require(header[..4].SequenceEqual("\u007fELF"u8));
            elfClass = header[4];
            byte data = header[5];
            byte osAbi = header[7];
            Require(elfClass is 1 or 2 && data == 1 && header[6] == 1);
            foreach (byte padding in header[9..16])
            {
                Require(padding == 0);
            }
            int headerSize = elfClass == 1 ? 52 : 64;
            ReadAt(stream, length, 16, header[16..headerSize]);
            ushort objectType = U16(header, 16);
            machine = U16(header, 18);
            Require(U32(header, 20) == 1);
            bool supportedObjectType = objectType is 1 or 2 or 3;
            Require(supportedObjectType || (allowForeignMetadata && (objectType is 0 or 4 || objectType >= 0xfe00)));
            uint headerFlags = U32(header, elfClass == 1 ? 36 : 48);
            bool supportedMachine = (elfClass == 1 && machine is 3 or 40) || (elfClass == 2 && machine is 62 or 183);
            bool supportedHeaderFlags = machine == 40
                ? (headerFlags & ~0x600u) == 0x05000000u && (headerFlags & 0x600u) != 0x600u
                : headerFlags == 0;
            // glibc libc-abis: GNU UNIQUE/ABSOLUTE, plus IFUNC on x86; other levels are not modelled.
            int maximumGnuAbi = machine is 3 or 62 ? 3 : 2;
            bool supportedVersion = header[8] == 0 || (osAbi == 3 && header[8] <= maximumGnuAbi);
            bool supportedAbi = supportedMachine && supportedHeaderFlags && osAbi is 0 or 3 && supportedVersion;
            Require(supportedAbi || allowForeignMetadata);
            bool opaqueSegments = allowForeignMetadata && (!supportedAbi || !supportedObjectType);
            ulong entry = Address(header, 24);
            ulong programOffset = Address(header, elfClass == 1 ? 28 : 32);
            ulong sectionOffset = Address(header, elfClass == 1 ? 32 : 40);
            int sizeOffset = elfClass == 1 ? 40 : 52;
            Require(U16(header, sizeOffset) == headerSize);
            ushort programSize = U16(header, sizeOffset + 2);
            ushort programCount = U16(header, sizeOffset + 4);
            ushort sectionSize = U16(header, sizeOffset + 6);
            ushort sectionCount = U16(header, sizeOffset + 8);
            ushort sectionNames = U16(header, sizeOffset + 10);
            int expectedProgramSize = elfClass == 1 ? 32 : 56;
            Require(programCount <= MaximumProgramHeaders);
            Require(programSize == expectedProgramSize || (programCount == 0 && programSize == 0));
            if (programCount == 0)
            {
                Require(programOffset == 0);
            }
            else
            {
                Require(programOffset >= (ulong)headerSize);
                FileRange(programOffset, (ulong)programCount * programSize);
            }
            ValidateSections(sectionOffset, sectionSize, sectionCount, sectionNames, objectType, headerSize);

            Segment? dynamic = null;
            string? interpreter = null;
            Span<byte> program = stackalloc byte[56];
            for (int index = 0; index < programCount; index++)
            {
                ReadAt(stream, length, programOffset + (ulong)index * programSize, program[..programSize]);
                uint type = U32(program, 0);
                uint flags = U32(program, elfClass == 1 ? 24 : 4);
                ulong offset = Address(program, elfClass == 1 ? 4 : 8);
                ulong address = Address(program, elfClass == 1 ? 8 : 16);
                ulong fileSize = Address(program, elfClass == 1 ? 16 : 32);
                ulong memorySize = Address(program, elfClass == 1 ? 20 : 40);
                ulong alignment = Address(program, elfClass == 1 ? 28 : 48);
                if (type == 0)
                {
                    continue;
                }
                Require(opaqueSegments || type is 1 or 2 or 3 or 4 or 6 or 7 or
                    0x6474e550 or 0x6474e551 or 0x6474e552 or 0x6474e553 or 0x6474e554 ||
                    (machine == 40 && type == 0x70000001)); // PT_ARM_EXIDX carries no loader path.
                Require(objectType != 1 && (opaqueSegments || (flags & ~7u) == 0));
                Require(fileSize <= memorySize || (opaqueSegments && type is not 1 and not 2 and not 3 and not 7));
                FileRange(offset, fileSize);
                AddressRange(address, memorySize);
                Require(alignment <= 1 || PowerOfTwo(alignment));
                if (type == 1 && alignment > 1)
                {
                    Require(address % alignment == offset % alignment);
                }
                var segment = new Segment(type, flags, offset, address, fileSize, memorySize);
                if (type == 1)
                {
                    loads.Add(segment);
                }
                else if (type == 2)
                {
                    Require(dynamic is null);
                    dynamic = segment;
                }
                else if (type == 3)
                {
                    Require(interpreter is null);
                    interpreter = ReadInterpreter(offset, fileSize);
                }
            }
            Require(objectType is not 2 and not 3 || loads.Count != 0);
            Require(objectType != 1 || entry == 0);
            if (entry != 0 && objectType is 2 or 3)
            {
                MemoryRange(machine == 40 ? entry & ~1UL : entry, 1, executable: true);
            }
            if (dynamic is not null)
            {
                Require(FileAddress(dynamic.Value.Address, dynamic.Value.FileSize) == dynamic.Value.Offset);
                ReadDynamic(dynamic.Value);
                ValidateMetadata();
            }

            string[] needed = neededOffsets.Count == 0 ? [] : new string[neededOffsets.Count];
            for (int index = 0; index < needed.Length; index++)
            {
                string name = StringAt(neededOffsets[index]);
                ValidateLiteralPath(name, allowName: true, allowOrigin: true);
                needed[index] = name;
            }
            string? soname = tags.TryGetValue(14, out ulong sonameOffset) ? StringAt(sonameOffset) : null;
            if (soname is not null)
            {
                ValidateLiteralPath(soname, allowName: true, allowOrigin: false);
            }
            return new NativeElfImage(objectType, machine, elfClass, data, osAbi, interpreter,
                needed.Length == 0 ? Array.Empty<string>() : Array.AsReadOnly(needed),
                PathComponents(15), PathComponents(29), soname,
                tags.GetValueOrDefault(30UL), tags.GetValueOrDefault(0x6ffffffbUL))
            {
                HasDynamicSegment = dynamic is not null,
                HasSupportedAbi = supportedAbi,
                AbiVersion = header[8],
            };
        }

        private string ReadInterpreter(ulong offset, ulong size)
        {
            Require(size is >= 2 and <= 4096);
            Span<byte> bytes = stackalloc byte[4096];
            ReadAt(stream, length, offset, bytes[..(int)size]);
            Require(bytes[(int)size - 1] == 0 && !bytes[..((int)size - 1)].Contains((byte)0));
            string interpreter = StrictUtf8.GetString(bytes[..((int)size - 1)]);
            ValidateLiteralPath(interpreter, allowName: false, allowOrigin: false);
            return interpreter;
        }

        private void ValidateSections(ulong offset, ushort size, ushort count, ushort names, ushort objectType, int headerSize)
        {
            int expectedSize = elfClass == 1 ? 40 : 64;
            Require(count <= MaximumSectionHeaders && names < 0xff00);
            Require(size == expectedSize || (count == 0 && size == 0));
            if (count == 0)
            {
                Require(offset == 0 && names == 0);
                return;
            }
            Require(offset >= (ulong)headerSize && names < count);
            FileRange(offset, (ulong)count * size);
            Span<byte> section = stackalloc byte[64];
            for (int index = 0; index < count; index++)
            {
                ReadAt(stream, length, offset + (ulong)index * size, section[..size]);
                uint type = U32(section, 4);
                if (index == 0)
                {
                    foreach (byte value in section[..size])
                    {
                        Require(value == 0); // Extended numbering is deliberately unsupported.
                    }
                    continue;
                }
                ulong address = Address(section, elfClass == 1 ? 12 : 16);
                ulong fileOffset = Address(section, elfClass == 1 ? 16 : 24);
                ulong fileSize = Address(section, elfClass == 1 ? 20 : 32);
                uint link = U32(section, elfClass == 1 ? 24 : 40);
                ulong alignment = Address(section, elfClass == 1 ? 32 : 48);
                ulong entrySize = Address(section, elfClass == 1 ? 36 : 56);
                AddressRange(address, fileSize);
                Require(alignment <= 1 || PowerOfTwo(alignment));
                Require(entrySize == 0 || fileSize % entrySize == 0);
                Require(link < count && (objectType != 1 || type != 6));
                if (type != 8)
                {
                    FileRange(fileOffset, fileSize);
                }
                if (index == names)
                {
                    Require(type == 3);
                }
            }
        }

        private void ReadDynamic(Segment segment)
        {
            int entrySize = WordSize * 2;
            Require(segment.FileSize != 0 && segment.FileSize % (ulong)entrySize == 0 &&
                segment.FileSize / (ulong)entrySize <= MaximumDynamicEntries);
            Span<byte> entry = stackalloc byte[16];
            bool terminated = false;
            for (ulong offset = 0; offset < segment.FileSize; offset += (ulong)entrySize)
            {
                ReadAt(stream, length, segment.Offset + offset, entry[..entrySize]);
                ulong tag = Address(entry, 0);
                ulong value = Address(entry, WordSize);
                if (tag == 0)
                {
                    // DT_NULL's union has no meaning. Stock linker padding can
                    // retain a nonzero value after the first terminator; glibc
                    // elf/get-dynamic-info.h stops solely on d_tag == DT_NULL.
                    // Non-null tags after termination still fail below.
                    terminated = true;
                    continue;
                }
                Require(!terminated && SupportedTag(tag));
                if (tag == 1)
                {
                    neededOffsets.Add(value);
                }
                else if (tags.TryGetValue(tag, out ulong existing))
                {
                    Require(existing == value);
                }
                else
                {
                    tags.Add(tag, value);
                }
            }
            Require(terminated);
            Require((tags.GetValueOrDefault(30UL) & ~SupportedFlags) == 0 &&
                (tags.GetValueOrDefault(0x6ffffffbUL) & ~SupportedFlags1) == 0);
        }

        private bool SupportedTag(ulong tag) => tag is >= 1 and <= 30 or >= 32 and <= 37 or
            0x6ffffdf5 or 0x6ffffdf8 or 0x6ffffef5 or 0x6ffffef6 or 0x6ffffef7 or
            0x6ffffff0 or 0x6ffffff9 or 0x6ffffffa or 0x6ffffffb or
            0x6ffffffc or 0x6ffffffd or 0x6ffffffe or 0x6fffffff ||
            (machine == 62 && tag is 0x70000000 or 0x70000001 or 0x70000003) ||
            (machine == 183 && tag is 0x70000001 or 0x70000003 or 0x70000005);

        private void ValidateMetadata()
        {
            bool hasStrings = tags.ContainsKey(5);
            Require(hasStrings == tags.ContainsKey(10));
            Require(hasStrings || (neededOffsets.Count == 0 && !tags.ContainsKey(14) &&
                !tags.ContainsKey(15) && !tags.ContainsKey(29)));
            if (hasStrings)
            {
                stringSize = tags[10];
                Require(stringSize is > 0 and <= MaximumStringTableBytes);
                stringOffset = FileAddress(tags[5], stringSize);
                Span<byte> boundary = stackalloc byte[1];
                ReadAt(stream, length, stringOffset, boundary);
                Require(boundary[0] == 0);
                ReadAt(stream, length, stringOffset + stringSize - 1, boundary);
                Require(boundary[0] == 0);
            }
            // AArch64 BTI/PAC tags share numbers with x86-64 PLT size tags.
            ReadOnlySpan<ulong> markers = machine == 183
                ? [16, 21, 22, 24, 0x70000001, 0x70000003, 0x70000005]
                : [16, 21, 22, 24];
            foreach (ulong marker in markers)
            {
                if (tags.TryGetValue(marker, out ulong value))
                {
                    Require(value == 0);
                }
            }
            bool hasSymbols = tags.ContainsKey(6);
            Require(hasSymbols == tags.ContainsKey(11));
            if (hasSymbols)
            {
                Require(hasStrings && tags[11] == (ulong)SymbolSize);
            }
            ulong? symbolCount = null;
            if (tags.TryGetValue(4, out ulong hashAddress))
            {
                Require(hasSymbols);
                ulong offset = FileAddress(hashAddress, 8);
                Span<byte> hash = stackalloc byte[8];
                ReadAt(stream, length, offset, hash);
                uint buckets = U32(hash, 0);
                uint count = U32(hash, 4);
                Require(buckets is > 0 and <= MaximumTableEntries && count is > 0 and <= MaximumTableEntries);
                ulong size = 8 + ((ulong)buckets + count) * 4;
                FileAddress(hashAddress, size);
                ValidateHashIndices(offset + 8, (ulong)buckets + count, count);
                symbolCount = count;
            }
            if (tags.TryGetValue(0x6ffffef5, out ulong gnuHashAddress))
            {
                Require(hasSymbols);
                ulong gnuCount = ValidateGnuHash(gnuHashAddress);
                Require(symbolCount is null || gnuCount <= symbolCount.Value);
                symbolCount ??= gnuCount;
            }
            if (hasSymbols)
            {
                ulong count = symbolCount ?? 1;
                ulong symbols = FileAddress(tags[6], count * (ulong)SymbolSize);
                Span<byte> symbol = stackalloc byte[24];
                for (ulong index = 0; index < count; index++)
                {
                    ReadAt(stream, length, symbols + index * (ulong)SymbolSize, symbol[..SymbolSize]);
                    Require(U32(symbol, 0) < stringSize);
                }
            }
            if (tags.TryGetValue(0x6ffffff0, out ulong versionSymbols))
            {
                Require(symbolCount is not null);
                FileAddress(versionSymbols, symbolCount.GetValueOrDefault() * 2);
            }
            if (tags.TryGetValue(34, out ulong symbolSections))
            {
                Require(symbolCount is not null);
                FileAddress(symbolSections, symbolCount.GetValueOrDefault() * 4);
            }
            ValidateRelocations(17, 18, 19, WordSize * 2);
            ValidateRelocations(7, 8, 9, WordSize * 3);
            ValidateRelocations(36, 35, 37, WordSize);
            bool hasPlt = tags.ContainsKey(23);
            Require(hasPlt == tags.ContainsKey(2) && hasPlt == tags.ContainsKey(20));
            if (hasPlt)
            {
                Require(tags[20] is 7 or 17);
                int size = tags[20] == 7 ? WordSize * 3 : WordSize * 2;
                ValidateRelocationRange(tags[23], tags[2], size);
            }
            ValidateRelativeCount(0x6ffffff9, 8, WordSize * 3);
            ValidateRelativeCount(0x6ffffffa, 18, WordSize * 2);
            ValidateArray(25, 27);
            ValidateArray(26, 28);
            ValidateArray(32, 33);
            ReadOnlySpan<ulong> pointers = [3, 12, 13, 0x6ffffef6, 0x6ffffef7];
            foreach (ulong pointer in pointers)
            {
                if (tags.TryGetValue(pointer, out ulong address) && address != 0)
                {
                    bool executable = pointer is 12 or 13 or 0x6ffffef6;
                    if (executable && machine == 40)
                    {
                        address &= ~1UL;
                    }
                    MemoryRange(address, executable ? 1UL : (ulong)WordSize, executable);
                }
            }
            ValidateVersions(0x6ffffffc, 0x6ffffffd, definitions: true);
            ValidateVersions(0x6ffffffe, 0x6fffffff, definitions: false);
            ValidateX86Plt();
        }

        private void ValidateX86Plt()
        {
            if (machine != 62)
            {
                return;
            }
            // glibc-2.41 sysdeps/x86_64/dl-machine.h consumes these as an
            // executable PLT range and entry size, never a native search selector.
            // x32 uses the same machine tags, but remains a foreign execution ABI.
            // https://github.com/bminor/glibc/blob/glibc-2.41/sysdeps/x86_64/dl-machine.h
            bool present = tags.ContainsKey(0x70000000);
            Require(present == tags.ContainsKey(0x70000001) && present == tags.ContainsKey(0x70000003));
            if (!present)
            {
                return;
            }
            ulong address = tags[0x70000000];
            ulong size = tags[0x70000001];
            ulong entrySize = tags[0x70000003];
            Require(entrySize >= 16 && size >= entrySize && size % entrySize == 0 &&
                size / entrySize <= MaximumTableEntries);
            FileAddress(address, size);
            MemoryRange(address, size, executable: true);
        }

        private void ValidateHashIndices(ulong offset, ulong count, ulong maximum)
        {
            Span<byte> buffer = stackalloc byte[4096];
            while (count != 0)
            {
                int entries = (int)Math.Min(count, 1024UL);
                ReadAt(stream, length, offset, buffer[..(entries * 4)]);
                for (int index = 0; index < entries; index++)
                {
                    Require(U32(buffer, index * 4) < maximum);
                }
                offset += (ulong)entries * 4;
                count -= (ulong)entries;
            }
        }

        private ulong ValidateGnuHash(ulong address)
        {
            ulong offset = FileAddress(address, 16);
            Span<byte> header = stackalloc byte[16];
            ReadAt(stream, length, offset, header);
            uint buckets = U32(header, 0);
            uint firstSymbol = U32(header, 4);
            uint bloomWords = U32(header, 8);
            Require(buckets is > 0 and <= MaximumTableEntries && firstSymbol is > 0 and <= MaximumTableEntries &&
                bloomWords <= MaximumTableEntries && PowerOfTwo(bloomWords) && U32(header, 12) < WordSize * 8);
            ulong prefixSize = 16 + (ulong)bloomWords * (ulong)WordSize + (ulong)buckets * 4;
            FileAddress(address, prefixSize);
            ulong bucketOffset = offset + 16 + (ulong)bloomWords * (ulong)WordSize;
            Span<byte> value = stackalloc byte[4];
            uint largest = 0;
            Span<byte> bucketBytes = stackalloc byte[4096];
            for (uint index = 0; index < buckets;)
            {
                int entries = (int)Math.Min(buckets - index, 1024u);
                ReadAt(stream, length, bucketOffset + (ulong)index * 4, bucketBytes[..(entries * 4)]);
                for (int entry = 0; entry < entries; entry++)
                {
                    uint bucket = U32(bucketBytes, entry * 4);
                    Require(bucket == 0 || (bucket >= firstSymbol && bucket < MaximumTableEntries));
                    largest = Math.Max(largest, bucket);
                }
                index += (uint)entries;
            }
            if (largest == 0)
            {
                return firstSymbol;
            }
            ulong chainAddress = Add(address, prefixSize);
            ulong count = largest;
            while (true)
            {
                Require(count < MaximumTableEntries);
                ReadAt(stream, length, FileAddress(Add(chainAddress, (count - firstSymbol) * 4), 4), value);
                count++;
                if ((U32(value, 0) & 1) != 0)
                {
                    break;
                }
            }
            FileAddress(address, Add(prefixSize, (count - firstSymbol) * 4));
            return count;
        }

        private void ValidateRelocations(ulong addressTag, ulong sizeTag, ulong entryTag, int entrySize)
        {
            bool present = tags.ContainsKey(addressTag);
            Require(present == tags.ContainsKey(sizeTag));
            if (tags.TryGetValue(entryTag, out ulong declaredSize))
            {
                Require(declaredSize == (ulong)entrySize);
            }
            Require(!present || tags.ContainsKey(entryTag));
            if (present)
            {
                if (addressTag == 36)
                {
                    ValidateRelr(tags[addressTag], tags[sizeTag]);
                }
                else
                {
                    ValidateRelocationRange(tags[addressTag], tags[sizeTag], entrySize);
                }
            }
        }

        private void ValidateRelr(ulong address, ulong size)
        {
            ulong offset = ValidateSizedRange(address, size, WordSize);
            Span<byte> entry = stackalloc byte[8];
            ulong nextAddress = 0;
            bool hasBase = false;
            int targets = 0;
            for (ulong consumed = 0; consumed < size; consumed += (ulong)WordSize)
            {
                ReadAt(stream, length, offset + consumed, entry[..WordSize]);
                ulong value = Address(entry, 0);
                if ((value & 1) == 0)
                {
                    Require(value % (ulong)WordSize == 0 && targets < MaximumTableEntries);
                    targets++;
                    MemoryRange(value, (ulong)WordSize);
                    nextAddress = Add(value, (ulong)WordSize);
                    hasBase = true;
                }
                else
                {
                    Require(hasBase);
                    ulong bitmap = value >> 1;
                    int count = BitOperations.PopCount(bitmap);
                    Require(count <= MaximumTableEntries - targets);
                    targets += count;
                    while (bitmap != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(bitmap);
                        MemoryRange(Add(nextAddress, (ulong)bit * (ulong)WordSize), (ulong)WordSize);
                        bitmap &= bitmap - 1;
                    }
                    nextAddress = Add(nextAddress, (ulong)(WordSize * 8 - 1) * (ulong)WordSize);
                }
                AddressRange(nextAddress, 0);
            }
        }

        private void ValidateRelocationRange(ulong address, ulong size, int entrySize)
        {
            // Ordinary REL/RELA/PLT records select no native paths. Installed
            // DSOs can contain millions; validate the full range without a walk.
            Require(size % (ulong)entrySize == 0);
            if (size != 0)
            {
                FileAddress(address, size);
            }
        }

        private void ValidateRelativeCount(ulong countTag, ulong sizeTag, int entrySize)
        {
            if (tags.TryGetValue(countTag, out ulong count))
            {
                Require(tags.TryGetValue(sizeTag, out ulong size) && count <= size / (ulong)entrySize);
            }
        }

        private void ValidateArray(ulong addressTag, ulong sizeTag)
        {
            bool present = tags.ContainsKey(addressTag);
            Require(present == tags.ContainsKey(sizeTag));
            if (present)
            {
                ValidateSizedRange(tags[addressTag], tags[sizeTag], WordSize);
            }
        }

        private ulong ValidateSizedRange(ulong address, ulong size, int entrySize)
        {
            Require(size % (ulong)entrySize == 0 && size / (ulong)entrySize <= MaximumTableEntries);
            return size == 0 ? 0 : FileAddress(address, size);
        }

        private void ValidateVersions(ulong addressTag, ulong countTag, bool definitions)
        {
            bool present = tags.ContainsKey(addressTag);
            Require(present == tags.ContainsKey(countTag));
            if (!present)
            {
                return;
            }
            Require(stringSize != 0 && tags[countTag] is > 0 and <= MaximumDynamicEntries);
            ulong address = tags[addressTag];
            int recordSize = definitions ? 20 : 16;
            int auxiliarySize = definitions ? 8 : 16;
            Span<byte> record = stackalloc byte[20];
            Span<byte> auxiliary = stackalloc byte[16];
            int visitedAuxiliaries = 0;
            for (ulong index = 0; index < tags[countTag]; index++)
            {
                ReadAt(stream, length, FileAddress(address, (ulong)recordSize), record[..recordSize]);
                Require(U16(record, 0) == 1);
                ushort count = U16(record, definitions ? 6 : 2);
                Require(count != 0 && count <= MaximumDynamicEntries - visitedAuxiliaries);
                visitedAuxiliaries += count;
                if (definitions)
                {
                    Require((U16(record, 2) & ~3) == 0);
                }
                else
                {
                    Require(StringAt(U32(record, 4)).Length != 0);
                }
                uint firstAuxiliary = U32(record, definitions ? 12 : 8);
                Require(firstAuxiliary >= recordSize);
                ulong auxiliaryAddress = Add(address, firstAuxiliary);
                for (int auxiliaryIndex = 0; auxiliaryIndex < count; auxiliaryIndex++)
                {
                    ReadAt(stream, length, FileAddress(auxiliaryAddress, (ulong)auxiliarySize), auxiliary[..auxiliarySize]);
                    if (!definitions)
                    {
                        Require((U16(auxiliary, 4) & ~2) == 0);
                    }
                    Require(StringAt(U32(auxiliary, definitions ? 0 : 8)).Length != 0);
                    uint nextAuxiliary = U32(auxiliary, definitions ? 4 : 12);
                    Require(auxiliaryIndex == count - 1 ? nextAuxiliary == 0 : nextAuxiliary >= auxiliarySize);
                    auxiliaryAddress = Add(auxiliaryAddress, nextAuxiliary);
                }
                uint next = U32(record, definitions ? 16 : 12);
                Require(index == tags[countTag] - 1 ? next == 0 : next >= recordSize);
                address = Add(address, next);
            }
        }

        private IReadOnlyList<string> PathComponents(ulong tag)
        {
            if (!tags.TryGetValue(tag, out ulong offset))
            {
                return Array.Empty<string>();
            }
            string value = StringAt(offset);
            // glibc decompose_rpath ignores a wholly empty tag BEFORE splitting.
            // Empty components in a nonempty tag are retained: they select cwd,
            // which can change inside Wine/CLR and is not an admitted search root.
            // https://github.com/bminor/glibc/blob/glibc-2.41/elf/dl-load.c
            return value.Length == 0 ? Array.Empty<string>() :
                Array.AsReadOnly(value.Split(':', StringSplitOptions.None));
        }

        private string StringAt(ulong offset)
        {
            Require(offset < stringSize);
            if (strings.TryGetValue(offset, out string? existing))
            {
                return existing;
            }
            stringBuffer ??= new byte[MaximumStringBytes];
            int maximum = (int)Math.Min(stringSize - offset, (ulong)MaximumStringBytes);
            int consumed = 0;
            while (consumed < maximum)
            {
                int count = Math.Min(4096, maximum - consumed);
                Span<byte> chunk = stringBuffer.AsSpan(consumed, count);
                ReadAt(stream, length, stringOffset + offset + (ulong)consumed, chunk);
                int terminator = chunk.IndexOf((byte)0);
                if (terminator >= 0)
                {
                    int size = consumed + terminator;
                    Require(size <= MaximumExtractedStringBytes - extractedStringBytes);
                    extractedStringBytes += size;
                    string value = StrictUtf8.GetString(stringBuffer.AsSpan(0, size));
                    Require(!ContainsControl(value));
                    strings.Add(offset, value);
                    return value;
                }
                consumed += count;
            }
            throw Failure();
        }

        private ulong FileAddress(ulong address, ulong size)
        {
            Require(size != 0);
            AddressRange(address, size);
            ulong end = Add(address, size);
            Segment? selected = null;
            foreach (Segment segment in loads)
            {
                ulong segmentEnd = Add(segment.Address, segment.MemorySize);
                if (address < segmentEnd && segment.Address < end)
                {
                    Require(selected is null && address >= segment.Address && end <= Add(segment.Address, segment.FileSize));
                    selected = segment;
                }
            }
            Require(selected is not null);
            return Add(selected.GetValueOrDefault().Offset, address - selected.GetValueOrDefault().Address);
        }

        private void MemoryRange(ulong address, ulong size, bool executable = false)
        {
            AddressRange(address, size);
            ulong end = Add(address, size);
            bool found = false;
            foreach (Segment segment in loads)
            {
                ulong segmentEnd = Add(segment.Address, segment.MemorySize);
                if (address < segmentEnd && segment.Address < end)
                {
                    Require(!found && address >= segment.Address && end <= segmentEnd &&
                        (!executable || (segment.Flags & 1) != 0));
                    found = true;
                }
            }
            Require(found);
        }

        private void AddressRange(ulong address, ulong size)
        {
            ulong end = Add(address, size);
            Require(elfClass != 1 || end <= 0x1_0000_0000UL);
        }

        private void FileRange(ulong offset, ulong size) => Require(offset <= length && size <= length - offset);
        private ulong Address(ReadOnlySpan<byte> bytes, int offset) => elfClass == 1 ? U32(bytes, offset) : U64(bytes, offset);
    }

    private static void ValidateLiteralPath(string value, bool allowName, bool allowOrigin)
    {
        Require(value.Length != 0 && !value.Contains('\\') && !ContainsControl(value));
        int start;
        bool origin = false;
        if (value[0] == '/')
        {
            start = 1;
        }
        else if (allowOrigin && value.StartsWith("$ORIGIN/", StringComparison.Ordinal))
        {
            start = 8;
            origin = true;
        }
        else
        {
            Require(allowName && !value.Contains('/') && !value.Contains('$') && value is not "." and not "..");
            return;
        }
        Require(start < value.Length && !value.AsSpan(start).Contains('$'));
        ReadOnlySpan<char> remaining = value.AsSpan(start);
        while (!remaining.IsEmpty)
        {
            int slash = remaining.IndexOf('/');
            ReadOnlySpan<char> component = slash < 0 ? remaining : remaining[..slash];
            Require(!component.IsEmpty && (origin || (!component.SequenceEqual(".") && !component.SequenceEqual(".."))));
            if (slash < 0)
            {
                break;
            }
            remaining = remaining[(slash + 1)..];
            Require(!remaining.IsEmpty);
        }
    }

    private static bool ContainsControl(string value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }
        return false;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
}
