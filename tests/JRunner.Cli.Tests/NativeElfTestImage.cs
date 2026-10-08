using System.Buffers.Binary;
using System.Text;

namespace JRunner.Cli.Tests;

// Deterministic, nonsecret metadata fixtures. These bytes are never executed.
internal sealed class NativeElfTestImage
{
    private readonly List<(ulong Tag, Func<NativeElfTestImage, ulong> Value)> tags = [];
    private readonly List<byte> strings = [0];
    private readonly List<Blob> blobs = [];
    private readonly List<(ulong Tag, ulong Value)> builtTags = [];

    internal NativeElfTestImage(byte elfClass = 2, ushort objectType = 3, ushort? machine = null)
    {
        ElfClass = elfClass;
        ObjectType = objectType;
        Machine = machine ?? (elfClass == 1 ? (ushort)3 : (ushort)62);
        HeaderFlags = Machine == 40 ? 0x05000400u : 0;
        HasDynamicSegment = objectType != 1;
        IncludeSections = objectType == 1;
    }

    internal byte ElfClass { get; set; }
    internal byte DataEncoding { get; set; } = 1;
    internal byte OsAbi { get; set; }
    internal byte AbiVersion { get; set; }
    internal byte IdentVersion { get; set; } = 1;
    internal uint HeaderVersion { get; set; } = 1;
    internal ushort ObjectType { get; set; }
    internal ushort Machine { get; set; }
    internal uint HeaderFlags { get; set; }
    internal ulong EntryPoint { get; set; }
    internal ulong BaseAddress { get; set; } = 0x10000;
    internal ulong BssBytes { get; set; } = 256;
    internal bool HasDynamicSegment { get; set; }
    internal bool IncludeStringTable { get; set; } = true;
    internal bool TerminateDynamic { get; set; } = true;
    internal int NullPaddingEntries { get; set; }
    internal string? Interpreter { get; set; }
    internal byte[]? InterpreterBytes { get; set; }
    internal int InterpreterCopies { get; set; } = 1;
    internal bool IncludeSections { get; set; }
    internal uint DataSectionType { get; set; } = 1;
    internal List<Segment> ExtraSegments { get; } = [];
    internal int HeaderSize => ElfClass == 1 ? 52 : 64;
    internal int ProgramHeaderSize => ElfClass == 1 ? 32 : 56;
    internal int SectionHeaderSize => ElfClass == 1 ? 40 : 64;
    internal int WordSize => ElfClass == 1 ? 4 : 8;
    internal int ProgramOffset => HeaderSize;
    internal int InterpreterOffset { get; private set; }
    internal int DynamicOffset { get; private set; }
    internal int StringTableOffset { get; private set; }
    internal int SectionTableOffset { get; private set; }
    internal int DataSectionOffset { get; private set; }
    internal ulong StringTableAddress => BaseAddress + (ulong)StringTableOffset;
    internal int StringTableSize => strings.Count;

    internal NativeElfTestImage AddTag(ulong tag, ulong value) => AddTag(tag, _ => value);

    internal NativeElfTestImage AddTag(ulong tag, Func<NativeElfTestImage, ulong> value)
    {
        tags.Add((tag, value));
        return this;
    }

    internal NativeElfTestImage AddTag(ulong tag, Blob blob) => AddTag(tag, _ => blob.Address);

    internal NativeElfTestImage AddStringTag(ulong tag, string value) => AddStringBytes(tag, Encoding.UTF8.GetBytes(value));

    internal NativeElfTestImage AddStringBytes(ulong tag, ReadOnlySpan<byte> value, bool terminated = true)
    {
        ulong offset = AppendString(value, terminated);
        return AddTag(tag, offset);
    }

    internal ulong AppendString(string value) => AppendString(Encoding.UTF8.GetBytes(value), terminated: true);

    internal ulong AppendString(ReadOnlySpan<byte> value, bool terminated = true)
    {
        ulong offset = (ulong)strings.Count;
        foreach (byte item in value)
        {
            strings.Add(item);
        }
        if (terminated)
        {
            strings.Add(0);
        }
        return offset;
    }

    internal Blob AddBlob(ReadOnlySpan<byte> bytes, int alignment = 8)
    {
        var blob = new Blob(bytes.ToArray(), alignment);
        blobs.Add(blob);
        return blob;
    }

    internal int ProgramHeaderOffset(int index) => ProgramOffset + index * ProgramHeaderSize;

    internal int DynamicEntryOffset(ulong tag, int occurrence = 0)
    {
        for (int index = 0; index < builtTags.Count; index++)
        {
            if (builtTags[index].Tag == tag && occurrence-- == 0)
            {
                return DynamicOffset + index * WordSize * 2;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(tag));
    }

    internal byte[] Build()
    {
        byte[]? interpreter = InterpreterBytes ?? (Interpreter is null ? null : Encoding.UTF8.GetBytes(Interpreter + "\0"));
        bool hasLoad = ObjectType != 1;
        int interpreterCount = interpreter is null ? 0 : InterpreterCopies;
        int programCount = (hasLoad ? 1 : 0) + interpreterCount + (HasDynamicSegment ? 1 : 0) + ExtraSegments.Count;
        int dynamicCount = HasDynamicSegment
            ? (IncludeStringTable ? 2 : 0) + tags.Count + (TerminateDynamic ? 1 : 0) + NullPaddingEntries
            : 0;
        InterpreterOffset = Align(ProgramOffset + programCount * ProgramHeaderSize, 8);
        DynamicOffset = Align(InterpreterOffset + (interpreter?.Length ?? 0), 8);
        StringTableOffset = Align(DynamicOffset + dynamicCount * WordSize * 2, 8);
        int next = StringTableOffset + strings.Count;
        foreach (Blob blob in blobs)
        {
            blob.Offset = Align(next, blob.Alignment);
            blob.Address = BaseAddress + (ulong)blob.Offset;
            next = blob.Offset + blob.Bytes.Length;
        }
        DataSectionOffset = Align(next, 8);
        if (IncludeSections)
        {
            next = DataSectionOffset + 8;
            SectionTableOffset = Align(next, 8);
            next = SectionTableOffset + SectionHeaderSize * 2;
        }
        else
        {
            SectionTableOffset = 0;
        }
        int imageSize = Align(next, 8);
        var bytes = new byte[imageSize];
        "\u007fELF"u8.CopyTo(bytes);
        bytes[4] = ElfClass;
        bytes[5] = DataEncoding;
        bytes[6] = IdentVersion;
        bytes[7] = OsAbi;
        bytes[8] = AbiVersion;
        Write16(bytes, 16, ObjectType);
        Write16(bytes, 18, Machine);
        Write32(bytes, 20, HeaderVersion);
        WriteAddress(bytes, 24, EntryPoint);
        WriteAddress(bytes, ElfClass == 1 ? 28 : 32, programCount == 0 ? 0UL : (ulong)ProgramOffset);
        WriteAddress(bytes, ElfClass == 1 ? 32 : 40, (ulong)SectionTableOffset);
        Write32(bytes, ElfClass == 1 ? 36 : 48, HeaderFlags);
        int sizeOffset = ElfClass == 1 ? 40 : 52;
        Write16(bytes, sizeOffset, (ushort)HeaderSize);
        Write16(bytes, sizeOffset + 2, (ushort)ProgramHeaderSize);
        Write16(bytes, sizeOffset + 4, (ushort)programCount);
        Write16(bytes, sizeOffset + 6, (ushort)SectionHeaderSize);
        Write16(bytes, sizeOffset + 8, IncludeSections ? (ushort)2 : (ushort)0);

        int program = 0;
        if (hasLoad)
        {
            WriteProgram(bytes, program++, new Segment(1, 0, BaseAddress, (ulong)imageSize, (ulong)imageSize + BssBytes, 7, 8));
        }
        for (int index = 0; index < interpreterCount; index++)
        {
            WriteProgram(bytes, program++, new Segment(3, (ulong)InterpreterOffset, BaseAddress + (ulong)InterpreterOffset,
                (ulong)interpreter!.Length, (ulong)interpreter.Length));
        }
        if (HasDynamicSegment)
        {
            ulong size = (ulong)(dynamicCount * WordSize * 2);
            WriteProgram(bytes, program++, new Segment(2, (ulong)DynamicOffset, BaseAddress + (ulong)DynamicOffset, size, size, 6, (ulong)WordSize));
        }
        foreach (Segment segment in ExtraSegments)
        {
            WriteProgram(bytes, program++, segment);
        }
        interpreter?.CopyTo(bytes, InterpreterOffset);
        for (int index = 0; index < strings.Count; index++)
        {
            bytes[StringTableOffset + index] = strings[index];
        }
        foreach (Blob blob in blobs)
        {
            blob.Bytes.CopyTo(bytes, blob.Offset);
        }
        builtTags.Clear();
        if (HasDynamicSegment)
        {
            if (IncludeStringTable)
            {
                builtTags.Add((5, StringTableAddress));
                builtTags.Add((10, (ulong)strings.Count));
            }
            foreach ((ulong tag, Func<NativeElfTestImage, ulong> value) in tags)
            {
                builtTags.Add((tag, value(this)));
            }
            if (TerminateDynamic)
            {
                builtTags.Add((0, 0));
            }
            for (int index = 0; index < NullPaddingEntries; index++)
            {
                builtTags.Add((0, 0));
            }
            for (int index = 0; index < builtTags.Count; index++)
            {
                int offset = DynamicOffset + index * WordSize * 2;
                WriteAddress(bytes, offset, builtTags[index].Tag);
                WriteAddress(bytes, offset + WordSize, builtTags[index].Value);
            }
        }
        if (IncludeSections)
        {
            "fixture\0"u8.CopyTo(bytes.AsSpan(DataSectionOffset));
            int section = SectionTableOffset + SectionHeaderSize;
            Write32(bytes, section + 4, DataSectionType);
            WriteAddress(bytes, section + (ElfClass == 1 ? 16 : 24), (ulong)DataSectionOffset);
            WriteAddress(bytes, section + (ElfClass == 1 ? 20 : 32), 8);
            WriteAddress(bytes, section + (ElfClass == 1 ? 32 : 48), 1);
        }
        return bytes;
    }

    internal void WriteAddress(byte[] bytes, int offset, ulong value)
    {
        if (ElfClass == 1)
        {
            Write32(bytes, offset, checked((uint)value));
        }
        else
        {
            Write64(bytes, offset, value);
        }
    }

    private void WriteProgram(byte[] bytes, int index, Segment segment)
    {
        int offset = ProgramHeaderOffset(index);
        Write32(bytes, offset, segment.Type);
        Write32(bytes, offset + (ElfClass == 1 ? 24 : 4), segment.Flags);
        WriteAddress(bytes, offset + (ElfClass == 1 ? 4 : 8), segment.Offset);
        WriteAddress(bytes, offset + (ElfClass == 1 ? 8 : 16), segment.Address);
        WriteAddress(bytes, offset + (ElfClass == 1 ? 16 : 32), segment.FileSize);
        WriteAddress(bytes, offset + (ElfClass == 1 ? 20 : 40), segment.MemorySize);
        WriteAddress(bytes, offset + (ElfClass == 1 ? 28 : 48), segment.Alignment);
    }

    internal static void Write16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    internal static void Write32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    internal static void Write64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private static int Align(int value, int alignment) => checked((value + alignment - 1) & -alignment);

    internal sealed class Blob(byte[] bytes, int alignment)
    {
        internal byte[] Bytes { get; } = bytes;
        internal int Alignment { get; } = alignment;
        internal int Offset { get; set; }
        internal ulong Address { get; set; }
    }

    internal sealed record Segment(uint Type, ulong Offset, ulong Address, ulong FileSize, ulong MemorySize, uint Flags = 4, ulong Alignment = 1);
}
