using System.Buffers.Binary;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class NativeElfReaderTests
{
    [Theory]
    [InlineData(1, 3, 1)]
    [InlineData(1, 3, 2)]
    [InlineData(1, 3, 3)]
    [InlineData(2, 62, 1)]
    [InlineData(2, 62, 2)]
    [InlineData(2, 62, 3)]
    [InlineData(1, 40, 1)]
    [InlineData(1, 40, 2)]
    [InlineData(2, 183, 1)]
    [InlineData(2, 183, 2)]
    public void Known_gnu_abi_feature_levels_are_supported(int elfClass, int machine, int abiVersion)
    {
        var fixture = new NativeElfTestImage((byte)elfClass, machine: (ushort)machine)
        {
            OsAbi = 3,
            AbiVersion = (byte)abiVersion,
        };

        NativeElfImage image = Parse(fixture.Build());

        Assert.True(image.HasSupportedAbi);
        Assert.Equal((byte)abiVersion, image.AbiVersion);
    }

    [Theory]
    [InlineData(1, 8, 0, 0, 0)]
    [InlineData(2, 243, 0, 0, 0)]
    [InlineData(2, 62, 6, 0, 0)]
    [InlineData(2, 62, 0, 1, 0)]
    [InlineData(2, 62, 0, 0, 1)]
    [InlineData(1, 40, 0, 0, 0x04000400)]
    [InlineData(1, 40, 0, 0, 0x05000600)]
    [InlineData(2, 62, 3, 4, 0)]
    [InlineData(1, 40, 3, 3, 0x05000400)]
    public void Foreign_module_metadata_preserves_paths_but_cannot_admit_an_unsupported_abi(
        int elfClass, int machine, int osAbi, int abiVersion, int headerFlags)
    {
        var fixture = new NativeElfTestImage((byte)elfClass, machine: (ushort)machine)
        {
            OsAbi = (byte)osAbi,
            AbiVersion = (byte)abiVersion,
            HeaderFlags = (uint)headerFlags,
            Interpreter = "/protected/ld-foreign.so",
        };
        fixture.AddStringTag(1, "/protected/libforeign.so").AddStringTag(29, "$ORIGIN");
        byte[] bytes = fixture.Build();

        NativeElfImage image = ParseModule(bytes);

        Assert.False(image.HasSupportedAbi);
        Assert.Equal((ushort)machine, image.Machine);
        Assert.Equal((byte)osAbi, image.OsAbi);
        Assert.Equal((byte)abiVersion, image.AbiVersion);
        Assert.Equal("/protected/ld-foreign.so", image.Interpreter);
        Assert.Equal(["/protected/libforeign.so"], image.Needed);
        Assert.Equal(["$ORIGIN"], image.RunPath);
        Reject(bytes);
    }

    [Fact]
    public void Foreign_relocatable_metadata_is_inspected_without_becoming_loadable_native_code()
    {
        var fixture = new NativeElfTestImage(2, 1, 243) { OsAbi = 255, AbiVersion = 1 };

        NativeElfImage image = ParseModule(fixture.Build());

        Assert.Equal((ushort)1, image.ObjectType);
        Assert.False(image.HasSupportedAbi);
        Assert.False(image.HasDynamicSegment);
        Assert.Empty(image.Needed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(0xfe00)]
    public void Foreign_data_object_types_are_structurally_inspected_but_strict_roots_reject_them(int objectType)
    {
        var fixture = new NativeElfTestImage(2, (ushort)objectType, 243) { HasDynamicSegment = false };
        byte[] bytes = fixture.Build();

        NativeElfImage image = ParseModule(bytes);

        Assert.Equal((ushort)objectType, image.ObjectType);
        Assert.False(image.HasSupportedAbi);
        Reject(bytes);
    }

    [Fact]
    public void Foreign_opaque_program_segments_still_have_file_range_checks()
    {
        var fixture = new NativeElfTestImage(2, machine: 243);
        fixture.ExtraSegments.Add(new NativeElfTestImage.Segment(0x70000020, 0, 0, 1, 0, 0xf0000007));
        byte[] bytes = fixture.Build();

        Assert.False(ParseModule(bytes).HasSupportedAbi);
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(2) + 8, ulong.MaxValue);
        RejectModule(bytes);
    }

    [Theory]
    [InlineData(0x6ffffefaUL)]
    [InlineData(0x6ffffefcUL)]
    [InlineData(0x7fffffffUL)]
    [InlineData(0x70000010UL)]
    public void Foreign_metadata_does_not_hide_common_or_unknown_processor_loader_selectors(ulong tag)
    {
        var fixture = new NativeElfTestImage(2, machine: 243);
        fixture.AddTag(tag, 0);

        RejectModule(fixture.Build());
    }

    [Fact]
    public void Foreign_metadata_rejects_unsafe_needed_paths_dynamic_truncation_and_unknown_flags()
    {
        var path = new NativeElfTestImage(2, machine: 243);
        path.AddStringTag(1, "../escape.so");
        RejectModule(path.Build());
        RejectModule(new NativeElfTestImage(2, machine: 243) { TerminateDynamic = false }.Build());
        var flags = new NativeElfTestImage(2, machine: 243);
        flags.AddTag(0x6ffffffb, 0x10);
        RejectModule(flags.Build());
        byte[] bytes = new NativeElfTestImage(2, machine: 243).Build();
        RejectModule(bytes[..^1]);
    }

    [Theory]
    [InlineData(4, 3)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    [InlineData(9, 1)]
    public void Foreign_metadata_never_weakens_ident_class_encoding_or_version_checks(int offset, int value)
    {
        byte[] bytes = new NativeElfTestImage(2, machine: 243).Build();
        bytes[offset] = (byte)value;

        RejectModule(bytes);
    }

    [Fact]
    public void Arm_exception_index_program_metadata_is_supported_without_being_a_loader_selector()
    {
        var fixture = new NativeElfTestImage(1, machine: 40);
        fixture.ExtraSegments.Add(new NativeElfTestImage.Segment(0x70000001, 0, fixture.BaseAddress, 8, 8, 4, 4));

        Assert.True(Parse(fixture.Build()).HasSupportedAbi);
    }

    [Fact]
    public void A_zero_filled_overlapping_load_cannot_change_a_file_backed_string_table_mapping()
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringTag(1, "libfixture.so");
        fixture.ExtraSegments.Add(new NativeElfTestImage.Segment(1, 0, fixture.BaseAddress, 0, 1));
        fixture.Build();
        fixture.ExtraSegments[0] = new NativeElfTestImage.Segment(1, 0, fixture.StringTableAddress, 0, (ulong)fixture.StringTableSize);

        Reject(fixture.Build());
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(0x10001UL)]
    [InlineData(0x10002UL)]
    [InlineData(0xfffffffffffffff8UL)]
    public void Relr_records_require_an_aligned_mapped_base_before_any_bitmap(ulong value)
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        NativeElfTestImage.Write64(bytes, TagBlobOffset(fixture, bytes, 36), value);

        Reject(bytes);
    }

    [Fact]
    public void Valid_relr_bitmaps_are_bounded_and_resolved_against_load_memory()
    {
        var fixture = new NativeElfTestImage();
        byte[] records = new byte[16];
        NativeElfTestImage.Write64(records, 0, fixture.BaseAddress + 64);
        NativeElfTestImage.Write64(records, 8, 3);
        fixture.AddTag(36, fixture.AddBlob(records)).AddTag(35, 16).AddTag(37, 8);

        Assert.True(Parse(fixture.Build()).HasDynamicSegment);
    }

    [Fact]
    public void Relr_bitmap_targets_cannot_escape_mapped_load_memory()
    {
        var fixture = new NativeElfTestImage();
        fixture.AddTag(36, fixture.AddBlob(new byte[16])).AddTag(35, 16).AddTag(37, 8);
        byte[] bytes = fixture.Build();
        int records = TagBlobOffset(fixture, bytes, 36);
        NativeElfTestImage.Write64(bytes, records, fixture.BaseAddress + (ulong)bytes.Length + fixture.BssBytes - 8);
        NativeElfTestImage.Write64(bytes, records + 8, 3);

        Reject(bytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public void Relr_address_and_size_tags_are_paired_and_require_the_word_entry_size(int present)
    {
        var fixture = new NativeElfTestImage();
        NativeElfTestImage.Blob records = fixture.AddBlob(new byte[8]);
        if ((present & 1) != 0)
        {
            fixture.AddTag(36, records);
        }
        if ((present & 2) != 0)
        {
            fixture.AddTag(35, 8);
        }
        if ((present & 4) != 0)
        {
            fixture.AddTag(37, 8);
        }

        Reject(fixture.Build());
    }

    [Theory]
    [InlineData(16UL)]
    [InlineData(21UL)]
    [InlineData(22UL)]
    [InlineData(24UL)]
    public void Dynamic_marker_tags_reject_nonzero_payloads(ulong tag)
    {
        var fixture = new NativeElfTestImage();
        fixture.AddTag(tag, 1);

        Reject(fixture.Build());
    }

    [Fact]
    public void Unsupported_stream_capabilities_are_redacted_inspection_failures()
    {
        using var nonseekable = new MetadataStream("\u007fELF"u8.ToArray(), 4, canSeek: false);
        using var unreadable = new MetadataStream("\u007fELF"u8.ToArray(), 4, canRead: false);

        AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(nonseekable)));
        AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(unreadable)));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public void Static_images_retain_class_and_object_type_without_inventing_dependencies(int elfClass, int objectType)
    {
        var fixture = new NativeElfTestImage((byte)elfClass, (ushort)objectType) { HasDynamicSegment = false };
        fixture.EntryPoint = fixture.BaseAddress + (ulong)fixture.HeaderSize;

        NativeElfImage image = Parse(fixture.Build());

        Assert.Equal((byte)elfClass, image.ElfClass);
        Assert.Equal((ushort)objectType, image.ObjectType);
        Assert.Equal((byte)1, image.DataEncoding);
        Assert.False(image.HasDynamicSegment);
        Assert.True(image.HasSupportedAbi);
        Assert.Equal((byte)0, image.AbiVersion);
        Assert.Null(image.Interpreter);
        Assert.Null(image.Soname);
        Assert.Empty(image.Needed);
        Assert.Empty(image.RPath);
        Assert.Empty(image.RunPath);
        Assert.Equal(0UL, image.Flags);
        Assert.Equal(0UL, image.Flags1);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public void Dynamic_exec_and_shared_objects_return_only_declared_paths(int elfClass, int objectType)
    {
        var fixture = new NativeElfTestImage((byte)elfClass, (ushort)objectType)
        {
            Interpreter = objectType == 2 ? "/protected/ld-linux-fixture.so" : null,
        };
        fixture.AddStringTag(1, "libfixture.so.1").AddStringTag(1, "/protected/libliteral.so")
            .AddStringTag(1, "$ORIGIN/modules/liborigin.so").AddStringTag(14, "libdeclared.so.1");

        NativeElfImage image = Parse(fixture.Build());

        Assert.True(image.HasDynamicSegment);
        Assert.Equal(fixture.Interpreter, image.Interpreter);
        Assert.Equal(["libfixture.so.1", "/protected/libliteral.so", "$ORIGIN/modules/liborigin.so"], image.Needed);
        Assert.Equal("libdeclared.so.1", image.Soname);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(1, 40)]
    [InlineData(2, 62)]
    [InlineData(2, 183)]
    public void Ordinary_glibc_and_dotnet_metadata_tables_are_supported_without_execution(int elfClass, int machine)
    {
        NativeElfTestImage fixture = MetadataShape((byte)elfClass, (ushort)machine);
        fixture.OsAbi = 3;

        NativeElfImage image = Parse(fixture.Build());

        Assert.Equal((ushort)machine, image.Machine);
        Assert.Equal((byte)3, image.OsAbi);
        Assert.Equal(["libc.so.6", "libpthread.so.0", "libdl.so.2"], image.Needed);
        Assert.Equal(["/protected/rpath", "$ORIGIN"], image.RPath);
        Assert.Equal(["$ORIGIN", "/protected/runtime"], image.RunPath);
        Assert.Equal("libcoreclr-fixture.so", image.Soname);
        Assert.Equal(0x1fUL, image.Flags);
        Assert.Equal(0x080008e9UL, image.Flags1);
    }

    [Fact]
    public void Paths_keep_empty_colon_components_and_tokens_for_closure_policy_validation()
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringTag(15, ":$ORIGIN::relative:$LIB:/absolute/../literal:")
            .AddStringTag(29, "$ORIGIN");

        NativeElfImage image = Parse(fixture.Build());

        Assert.Equal(["", "$ORIGIN", "", "relative", "$LIB", "/absolute/../literal", ""], image.RPath);
        Assert.Equal(["$ORIGIN"], image.RunPath);
    }

    [Fact]
    public void Wholly_empty_rpath_and_runpath_contribute_no_search_directories()
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringTag(15, "").AddStringTag(29, "");

        NativeElfImage image = Parse(fixture.Build());

        Assert.Empty(image.RPath);
        Assert.Empty(image.RunPath);
    }

    [Theory]
    [InlineData("libfixture.so")]
    [InlineData("libválid.so")]
    [InlineData("/protected/libfixture.so")]
    [InlineData("$ORIGIN/libfixture.so")]
    [InlineData("$ORIGIN/modules/libfixture.so")]
    [InlineData("$ORIGIN/../libfixture.so")]
    [InlineData("$ORIGIN/./libfixture.so")]
    public void Needed_supports_literal_names_absolute_paths_and_approved_origin_suffixes(string name)
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringTag(1, name);

        Assert.Equal([name], Parse(fixture.Build()).Needed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("relative/libfixture.so")]
    [InlineData("../libfixture.so")]
    [InlineData("$ORIGIN")]
    [InlineData("${ORIGIN}/libfixture.so")]
    [InlineData("$ORIGIN_SUFFIX/libfixture.so")]
    [InlineData("$LIB/libfixture.so")]
    [InlineData("$PLATFORM/libfixture.so")]
    [InlineData("$UNKNOWN/libfixture.so")]
    [InlineData("$ORIGIN//libfixture.so")]
    [InlineData("$ORIGIN/")]
    [InlineData("$ORIGIN/$LIB/libfixture.so")]
    [InlineData("/protected/$ORIGIN/libfixture.so")]
    [InlineData("/protected/../libfixture.so")]
    [InlineData("/protected//libfixture.so")]
    [InlineData("/protected/libfixture.so/")]
    [InlineData("lib\\fixture.so")]
    [InlineData("lib\nfixture.so")]
    public void Needed_rejects_unsafe_relative_and_token_paths(string name)
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringTag(1, name);

        Reject(fixture.Build());
    }

    [Theory]
    [InlineData("ld-linux.so")]
    [InlineData("./ld-linux.so")]
    [InlineData("$ORIGIN/ld-linux.so")]
    [InlineData("/lib/$LIB/ld-linux.so")]
    [InlineData("/lib/../ld-linux.so")]
    [InlineData("/lib/./ld-linux.so")]
    [InlineData("/lib//ld-linux.so")]
    [InlineData("/lib/ld-linux.so/")]
    [InlineData("/lib\\ld-linux.so")]
    [InlineData("/lib/ld\nlinux.so")]
    public void Interpreter_requires_an_absolute_literal_path(string interpreter)
    {
        Reject(new NativeElfTestImage(2, 2) { Interpreter = interpreter }.Build());
    }

    [Fact]
    public void Interpreter_accepts_strict_utf8_literal_paths()
    {
        var fixture = new NativeElfTestImage(2, 2) { Interpreter = "/protected/ld-válid.so" };

        Assert.Equal(fixture.Interpreter, Parse(fixture.Build()).Interpreter);
    }

    [Fact]
    public void Duplicate_interpreters_are_rejected_even_when_identical()
    {
        Reject(new NativeElfTestImage(2, 2) { Interpreter = "/protected/ld.so", InterpreterCopies = 2 }.Build());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Interpreter_rejects_empty_nonterminated_embedded_null_and_invalid_utf8(int invalidShape)
    {
        byte[] bytes = invalidShape switch
        {
            0 => [0],
            1 => "/protected/ld.so"u8.ToArray(),
            2 => "/protected/ld.so\0ignored\0"u8.ToArray(),
            _ => [(byte)'/', 0xc3, 0x28, 0],
        };
        Reject(new NativeElfTestImage(2, 2) { InterpreterBytes = bytes }.Build());
    }

    [Fact]
    public void Interpreter_size_is_bounded()
    {
        Reject(new NativeElfTestImage(2, 2) { Interpreter = "/" + new string('a', 4096) }.Build());
    }

    [Theory]
    [InlineData(0x6ffffefaUL)] // CONFIG
    [InlineData(0x6ffffefbUL)] // DEPAUDIT
    [InlineData(0x6ffffefcUL)] // AUDIT
    [InlineData(0x7fffffffUL)] // FILTER
    [InlineData(0x7ffffffdUL)] // AUXILIARY
    [InlineData(0x6ffffef8UL)] // GNU_CONFLICT
    [InlineData(0x6ffffef9UL)] // GNU_LIBLIST
    [InlineData(0x6ffffdf6UL)] // GNU_CONFLICTSZ
    [InlineData(0x6ffffdf7UL)] // GNU_LIBLISTSZ
    [InlineData(0x6ffffdfcUL)] // FEATURE_1
    [InlineData(0x6ffffdfdUL)] // POSFLAG_1
    [InlineData(0x6ffffefdUL)] // PLTPAD
    [InlineData(0x6ffffefeUL)] // MOVETAB
    [InlineData(0x6ffffeffUL)] // SYMINFO
    [InlineData(0x6000000eUL)] // Unmodelled Solaris selector
    [InlineData(31UL)]
    [InlineData(38UL)]
    [InlineData(0x70000005UL)] // AArch64-only tag in an x86 object
    [InlineData(0xffffffffffffffffUL)]
    public void Loader_selectors_and_unknown_dynamic_tags_fail_closed_even_with_zero_values(ulong tag)
    {
        var fixture = new NativeElfTestImage();
        fixture.AddTag(tag, 0);

        Reject(fixture.Build());
    }

    [Theory]
    [InlineData((byte)2, 16UL)]
    [InlineData((byte)2, 32UL)]
    [InlineData((byte)1, 16UL)]
    [InlineData((byte)1, 32UL)]
    public void Glibc_x86_plt_metadata_is_bounded_code_metadata_not_a_loader_selector(byte elfClass, ulong entrySize)
    {
        var fixture = new NativeElfTestImage(elfClass, machine: 62);
        NativeElfTestImage.Blob plt = fixture.AddBlob(new byte[64], alignment: 16);
        fixture.AddTag(0x70000000, plt).AddTag(0x70000001, 64).AddTag(0x70000003, entrySize);
        byte[] bytes = fixture.Build();

        NativeElfImage image = elfClass == 2 ? Parse(bytes) : ParseModule(bytes);

        Assert.Equal(elfClass == 2, image.HasSupportedAbi);
        if (elfClass == 1)
        {
            Reject(bytes); // x32 metadata is not permission to execute its ABI.
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void X86_plt_metadata_requires_the_complete_address_size_and_entry_triple(int present)
    {
        var fixture = new NativeElfTestImage();
        NativeElfTestImage.Blob plt = fixture.AddBlob(new byte[32], alignment: 16);
        if ((present & 1) != 0)
        {
            fixture.AddTag(0x70000000, plt);
        }
        if ((present & 2) != 0)
        {
            fixture.AddTag(0x70000001, 32);
        }
        if ((present & 4) != 0)
        {
            fixture.AddTag(0x70000003, 16);
        }

        Reject(fixture.Build());
    }

    [Theory]
    [InlineData(0UL, 32UL)]
    [InlineData(8UL, 32UL)]
    [InlineData(16UL, 0UL)]
    [InlineData(16UL, 8UL)]
    [InlineData(16UL, 17UL)]
    [InlineData(32UL, 48UL)]
    [InlineData(16UL, ulong.MaxValue)]
    public void X86_plt_metadata_rejects_invalid_table_shapes(ulong entrySize, ulong size)
    {
        var fixture = new NativeElfTestImage();
        NativeElfTestImage.Blob plt = fixture.AddBlob(new byte[64], alignment: 16);
        fixture.AddTag(0x70000000, plt).AddTag(0x70000001, size).AddTag(0x70000003, entrySize);

        Reject(fixture.Build());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void X86_plt_metadata_requires_file_backed_executable_bytes(bool bss)
    {
        var fixture = new NativeElfTestImage();
        NativeElfTestImage.Blob plt = fixture.AddBlob(new byte[32], alignment: 16);
        fixture.AddTag(0x70000000, plt).AddTag(0x70000001, 32).AddTag(0x70000003, 16);
        byte[] bytes = fixture.Build();
        if (bss)
        {
            fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(0x70000000) + fixture.WordSize,
                fixture.BaseAddress + (ulong)bytes.Length);
        }
        else
        {
            NativeElfTestImage.Write32(bytes, fixture.ProgramHeaderOffset(0) + 4, 6);
        }

        Reject(bytes);
    }

    [Theory]
    [InlineData((byte)1, (ushort)3)]
    [InlineData((byte)2, (ushort)183)]
    public void X86_plt_tags_are_not_reinterpreted_for_other_machines(byte elfClass, ushort machine)
    {
        var fixture = new NativeElfTestImage(elfClass, machine: machine);
        NativeElfTestImage.Blob plt = fixture.AddBlob(new byte[32], alignment: 16);
        fixture.AddTag(0x70000000, plt).AddTag(0x70000001, 32).AddTag(0x70000003, 16);

        RejectModule(fixture.Build());
    }


    [Theory]
    [InlineData(30UL, 0x20UL)]
    [InlineData(30UL, 0x100000000UL)]
    [InlineData(0x6ffffffbUL, 0x2UL)] // Unsupported GLOBAL
    [InlineData(0x6ffffffbUL, 0x4UL)] // Unsupported GROUP
    [InlineData(0x6ffffffbUL, 0x10UL)] // LOADFLTR
    [InlineData(0x6ffffffbUL, 0x2000UL)] // CONFALT
    [InlineData(0x6ffffffbUL, 0x4000UL)] // ENDFILTEE
    [InlineData(0x6ffffffbUL, 0x01000000UL)] // GLOBAUDIT
    [InlineData(0x6ffffffbUL, 0x20000000UL)] // WEAKFILTER
    [InlineData(0x6ffffffbUL, 0x8000000000000000UL)]
    public void Unknown_or_path_opening_flags_fail_closed(ulong tag, ulong value)
    {
        var fixture = new NativeElfTestImage();
        fixture.AddTag(tag, value);

        Reject(fixture.Build());
    }

    [Fact]
    public void Repeated_needed_entries_and_identical_singletons_are_preserved_consistently()
    {
        var fixture = new NativeElfTestImage();
        ulong soname = fixture.AppendString("libfixture.so");
        ulong path = fixture.AppendString("$ORIGIN");
        fixture.AddTag(1, soname).AddTag(1, soname).AddTag(14, soname).AddTag(14, soname)
            .AddTag(15, path).AddTag(15, path).AddTag(29, path).AddTag(29, path)
            .AddTag(5, item => item.StringTableAddress).AddTag(10, item => (ulong)item.StringTableSize)
            .AddTag(30, 1).AddTag(30, 1).AddTag(0x6ffffffb, 0x880).AddTag(0x6ffffffb, 0x880);

        NativeElfImage image = Parse(fixture.Build());

        Assert.Equal(["libfixture.so", "libfixture.so"], image.Needed);
        Assert.Equal("libfixture.so", image.Soname);
        Assert.Equal(["$ORIGIN"], image.RPath);
        Assert.Equal(["$ORIGIN"], image.RunPath);
        Assert.Equal(1UL, image.Flags);
        Assert.Equal(0x880UL, image.Flags1);
        Assert.Same(image.Needed[0], image.Needed[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)image.Needed)[0] = "replacement");
    }

    [Theory]
    [InlineData(5UL)]
    [InlineData(10UL)]
    [InlineData(14UL)]
    [InlineData(15UL)]
    [InlineData(29UL)]
    [InlineData(30UL)]
    [InlineData(0x6ffffffbUL)]
    public void Conflicting_singletons_are_rejected(ulong tag)
    {
        var fixture = new NativeElfTestImage();
        if (tag is 14 or 15 or 29)
        {
            fixture.AddStringTag(tag, "first").AddStringTag(tag, "second");
        }
        else if (tag is 5 or 10)
        {
            fixture.AddTag(tag, 0);
        }
        else
        {
            fixture.AddTag(tag, 0).AddTag(tag, 1);
        }

        Reject(fixture.Build());
    }

    [Fact]
    public void Dynamic_null_only_still_reports_dynamic_segment_presence()
    {
        var fixture = new NativeElfTestImage() { IncludeStringTable = false, NullPaddingEntries = 2 };

        NativeElfImage image = Parse(fixture.Build());

        Assert.True(image.HasDynamicSegment);
        Assert.Empty(image.Needed);
        Assert.Null(image.Interpreter);
    }

    [Fact]
    public void Dynamic_tables_require_a_null_terminator()
    {
        Reject(new NativeElfTestImage() { TerminateDynamic = false }.Build());
    }

    [Fact]
    public void Nonnull_tags_after_dynamic_termination_are_rejected()
    {
        var fixture = new NativeElfTestImage();
        fixture.AddTag(0, 0).AddStringTag(1, "hidden.so");

        Reject(fixture.Build());
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    public void Dynamic_null_union_payloads_are_unused_in_terminators_and_padding(byte elfClass)
    {
        // Stock Ubuntu libxkbcommon-x11 ends its PT_DYNAMIC with (DT_NULL,0),
        // (DT_NULL,0xb4e), then zero padding. glibc stops on d_tag, not d_un.
        var fixture = new NativeElfTestImage(elfClass) { NullPaddingEntries = 2 };
        byte[] bytes = fixture.Build();
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(0, 0) + fixture.WordSize, 1);
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(0, 1) + fixture.WordSize, 0xb4e);

        NativeElfImage image = Parse(bytes);

        Assert.True(image.HasDynamicSegment);
        Assert.Empty(image.Needed);
    }

    [Fact]
    public void Dynamic_segment_entry_size_and_count_are_bounded()
    {
        var fixture = new NativeElfTestImage();
        byte[] bytes = fixture.Build();
        int dynamicProgram = fixture.ProgramHeaderOffset(1);
        NativeElfTestImage.Write64(bytes, dynamicProgram + 32, 17);
        Reject(bytes);

        var oversized = new NativeElfTestImage();
        for (int index = 0; index < NativeElfReader.MaximumDynamicEntries; index++)
        {
            oversized.AddTag(30, 0);
        }
        Reject(oversized.Build());
    }

    [Fact]
    public void Duplicate_dynamic_segments_are_rejected()
    {
        var fixture = new NativeElfTestImage();
        fixture.ExtraSegments.Add(new NativeElfTestImage.Segment(2, 0, 0, 0, 0));

        Reject(fixture.Build());
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(14UL)]
    [InlineData(15UL)]
    [InlineData(29UL)]
    public void Extracted_strings_require_strict_utf8(ulong tag)
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringBytes(tag, [0xc3, 0x28]);
        OperationFailureException failure = Reject(fixture.Build());

        Assert.DoesNotContain("\ufffd", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(14UL)]
    [InlineData(15UL)]
    [InlineData(29UL)]
    public void Extracted_string_offsets_must_be_inside_the_declared_table(ulong tag)
    {
        var fixture = new NativeElfTestImage();
        fixture.AddTag(tag, 1);

        Reject(fixture.Build());
    }

    [Fact]
    public void String_tables_require_first_and_last_null_boundaries()
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringTag(1, "libfixture.so");
        byte[] first = fixture.Build();
        first[fixture.StringTableOffset] = 1;
        Reject(first);
        byte[] last = fixture.Build();
        last[fixture.StringTableOffset + fixture.StringTableSize - 1] = 1;
        Reject(last);
    }

    [Fact]
    public void Strings_require_termination_within_the_bounded_extraction_size()
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringTag(1, new string('a', NativeElfReader.MaximumStringBytes));

        Reject(fixture.Build());
    }

    [Fact]
    public void Unterminated_dependency_bytes_are_rejected_without_partial_names()
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringBytes(1, "libfixture.so"u8, terminated: false);

        Reject(fixture.Build());
    }

    [Fact]
    public void Total_extracted_strings_are_bounded()
    {
        var fixture = new NativeElfTestImage();
        string name = new('a', NativeElfReader.MaximumStringBytes - 1);
        for (int index = 0; index < 65; index++)
        {
            fixture.AddStringTag(1, name);
        }

        Reject(fixture.Build());
    }

    [Theory]
    [InlineData(5UL)]
    [InlineData(10UL)]
    public void String_table_pointer_and_size_are_required_as_a_pair(ulong loneTag)
    {
        var fixture = new NativeElfTestImage() { IncludeStringTable = false };
        fixture.AddTag(loneTag, 1);

        Reject(fixture.Build());
    }

    [Fact]
    public void String_table_sizes_are_bounded_before_allocating_or_reading_strings()
    {
        var fixture = new NativeElfTestImage();
        byte[] bytes = fixture.Build();
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(10) + fixture.WordSize, 32 * 1024 * 1024 + 1);

        Reject(bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void String_table_load_translation_rejects_full_and_partial_ambiguity(bool partial)
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringTag(1, "libfixture.so");
        fixture.ExtraSegments.Add(new NativeElfTestImage.Segment(1, 0, fixture.BaseAddress, 1, 1));
        fixture.Build();
        ulong displacement = partial ? 1UL : 0;
        ulong size = partial ? 1UL : (ulong)fixture.StringTableSize;
        fixture.ExtraSegments[0] = new NativeElfTestImage.Segment(1, (ulong)fixture.StringTableOffset + displacement,
            fixture.StringTableAddress + displacement, size, size);

        Reject(fixture.Build());
    }

    [Fact]
    public void String_tables_cannot_be_read_from_zero_filled_load_memory()
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringTag(1, "libfixture.so");
        byte[] bytes = fixture.Build();
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(5) + fixture.WordSize,
            fixture.BaseAddress + (ulong)bytes.Length + 1);

        Reject(bytes);
    }

    [Fact]
    public void Dynamic_segments_must_agree_with_file_backed_load_translation()
    {
        var fixture = new NativeElfTestImage();
        byte[] bytes = fixture.Build();
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(1) + 16,
            fixture.BaseAddress + (ulong)fixture.DynamicOffset + 1);

        Reject(bytes);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(51)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(111)]
    public void Magic_bearing_truncated_images_fail_instead_of_returning_non_elf(int size)
    {
        byte[] bytes = new NativeElfTestImage().Build();

        Reject(bytes[..size]);
    }

    [Fact]
    public void Segment_truncation_is_rejected_even_when_loader_tags_are_intact()
    {
        byte[] bytes = new NativeElfTestImage().Build();

        Reject(bytes[..^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void File_offsets_virtual_ranges_and_string_addresses_reject_overflow(int shape)
    {
        var fixture = new NativeElfTestImage();
        byte[] bytes = fixture.Build();
        int load = fixture.ProgramHeaderOffset(0);
        switch (shape)
        {
            case 0:
                NativeElfTestImage.Write64(bytes, 32, ulong.MaxValue - 8);
                break;
            case 1:
                NativeElfTestImage.Write64(bytes, load + 8, ulong.MaxValue - 8);
                NativeElfTestImage.Write64(bytes, load + 32, 16);
                NativeElfTestImage.Write64(bytes, load + 40, 16);
                break;
            case 2:
                NativeElfTestImage.Write64(bytes, load + 16, ulong.MaxValue - 8);
                NativeElfTestImage.Write64(bytes, load + 32, 0);
                NativeElfTestImage.Write64(bytes, load + 40, 16);
                break;
            default:
                fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(5) + fixture.WordSize, ulong.MaxValue - 1);
                fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(10) + fixture.WordSize, 8);
                break;
        }

        Reject(bytes);
    }

    [Fact]
    public void Elf32_virtual_address_ranges_cannot_wrap_the_class_address_space()
    {
        var fixture = new NativeElfTestImage(1);
        byte[] bytes = fixture.Build();
        int load = fixture.ProgramHeaderOffset(0);
        NativeElfTestImage.Write32(bytes, load + 8, uint.MaxValue - 7);
        NativeElfTestImage.Write32(bytes, load + 16, 0);
        NativeElfTestImage.Write32(bytes, load + 20, 16);

        Reject(bytes);
    }

    [Fact]
    public void Load_segments_require_file_size_memory_size_and_alignment_consistency()
    {
        var fixture = new NativeElfTestImage();
        byte[] tooSmall = fixture.Build();
        NativeElfTestImage.Write64(tooSmall, fixture.ProgramHeaderOffset(0) + 40, 1);
        Reject(tooSmall);
        byte[] nonPowerOfTwo = fixture.Build();
        NativeElfTestImage.Write64(nonPowerOfTwo, fixture.ProgramHeaderOffset(0) + 48, 3);
        Reject(nonPowerOfTwo);
        byte[] incongruent = fixture.Build();
        NativeElfTestImage.Write64(incongruent, fixture.ProgramHeaderOffset(0) + 16, fixture.BaseAddress + 1);
        Reject(incongruent);
    }

    [Fact]
    public void Program_header_count_entry_size_and_extended_numbering_are_rejected()
    {
        var fixture = new NativeElfTestImage();
        byte[] count = fixture.Build();
        NativeElfTestImage.Write16(count, 56, NativeElfReader.MaximumProgramHeaders + 1);
        Reject(count);
        byte[] entry = fixture.Build();
        NativeElfTestImage.Write16(entry, 54, 55);
        Reject(entry);
        byte[] extended = fixture.Build();
        NativeElfTestImage.Write16(extended, 56, 0xffff);
        Reject(extended);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 3)]
    [InlineData(5, 0)]
    [InlineData(5, 2)]
    [InlineData(6, 0)]
    [InlineData(6, 2)]
    [InlineData(7, 6)]
    [InlineData(7, 9)]
    [InlineData(8, 1)]
    [InlineData(9, 1)]
    [InlineData(15, 1)]
    public void Unsupported_class_encoding_version_osabi_and_ident_padding_fail_closed(int offset, int value)
    {
        byte[] bytes = new NativeElfTestImage().Build();
        bytes[offset] = (byte)value;

        Reject(bytes);
    }

    [Theory]
    [InlineData(1, 62)]
    [InlineData(1, 183)]
    [InlineData(2, 3)]
    [InlineData(2, 40)]
    [InlineData(2, 8)]
    [InlineData(2, 0)]
    public void Unsupported_machine_and_class_pairs_fail_closed(int elfClass, int machine)
    {
        Reject(new NativeElfTestImage((byte)elfClass, machine: (ushort)machine).Build());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(0xff00)]
    public void Unsupported_object_types_fail_closed(int objectType)
    {
        Reject(new NativeElfTestImage(objectType: (ushort)objectType).Build());
    }

    [Fact]
    public void Header_version_size_and_architecture_flags_are_validated()
    {
        Reject(new NativeElfTestImage() { HeaderVersion = 2 }.Build());
        Reject(new NativeElfTestImage() { HeaderFlags = 1 }.Build());
        Reject(new NativeElfTestImage(1, machine: 40) { HeaderFlags = 0x05000600 }.Build());
        Reject(new NativeElfTestImage(1, machine: 40) { HeaderFlags = 0x04000400 }.Build());
        byte[] size = new NativeElfTestImage().Build();
        NativeElfTestImage.Write16(size, 52, 63);
        Reject(size);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(1, 40)]
    [InlineData(2, 62)]
    [InlineData(2, 183)]
    public void Relocatable_metadata_is_parsed_as_nonexecutable_with_no_dynamic_paths(int elfClass, int machine)
    {
        var fixture = new NativeElfTestImage((byte)elfClass, 1, (ushort)machine);

        NativeElfImage image = Parse(fixture.Build());

        Assert.Equal((ushort)1, image.ObjectType);
        Assert.False(image.HasDynamicSegment);
        Assert.Null(image.Interpreter);
        Assert.Empty(image.Needed);
        Assert.Empty(image.RPath);
        Assert.Empty(image.RunPath);
    }

    [Fact]
    public void Relocatable_objects_cannot_smuggle_program_or_dynamic_section_paths()
    {
        Reject(new NativeElfTestImage(objectType: 1) { HasDynamicSegment = true }.Build());
        Reject(new NativeElfTestImage(objectType: 1) { Interpreter = "/protected/ld.so" }.Build());
        Reject(new NativeElfTestImage(objectType: 1) { DataSectionType = 6 }.Build());
    }

    [Fact]
    public void Relocatable_section_tables_have_bounded_offsets_sizes_counts_and_links()
    {
        var fixture = new NativeElfTestImage(objectType: 1);
        byte[] bytes = fixture.Build();
        NativeElfTestImage.Write64(bytes, 40, ulong.MaxValue - 8);
        Reject(bytes);
        bytes = fixture.Build();
        NativeElfTestImage.Write64(bytes, fixture.SectionTableOffset + fixture.SectionHeaderSize + 24, ulong.MaxValue);
        Reject(bytes);
        bytes = fixture.Build();
        NativeElfTestImage.Write16(bytes, 60, NativeElfReader.MaximumSectionHeaders + 1);
        Reject(bytes);
        bytes = fixture.Build();
        NativeElfTestImage.Write32(bytes, fixture.SectionTableOffset + fixture.SectionHeaderSize + 40, 2);
        Reject(bytes);
        bytes = fixture.Build();
        NativeElfTestImage.Write32(bytes, fixture.SectionTableOffset + 4, 1);
        Reject(bytes);
    }

    [Theory]
    [InlineData(4UL)]
    [InlineData(6UL)]
    [InlineData(7UL)]
    [InlineData(17UL)]
    [InlineData(23UL)]
    [InlineData(25UL)]
    [InlineData(26UL)]
    [InlineData(32UL)]
    [InlineData(34UL)]
    [InlineData(36UL)]
    [InlineData(0x6ffffef5UL)]
    [InlineData(0x6ffffff0UL)]
    [InlineData(0x6ffffffcUL)]
    [InlineData(0x6ffffffeUL)]
    public void Nonpath_dynamic_tables_must_stay_in_unique_file_backed_load_ranges(ulong tag)
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(tag) + fixture.WordSize,
            fixture.BaseAddress + (ulong)bytes.Length + fixture.BssBytes + 1);

        Reject(bytes);
    }

    [Theory]
    [InlineData(9UL, 23UL)]
    [InlineData(19UL, 15UL)]
    [InlineData(37UL, 7UL)]
    [InlineData(11UL, 23UL)]
    [InlineData(20UL, 19UL)]
    [InlineData(8UL, 1UL)]
    [InlineData(18UL, 1UL)]
    [InlineData(35UL, 1UL)]
    [InlineData(27UL, 1UL)]
    [InlineData(0x6ffffff9UL, 2UL)]
    [InlineData(0x6ffffffaUL, 2UL)]
    public void Nonpath_table_entry_sizes_total_sizes_and_relative_counts_are_consistent(ulong tag, ulong value)
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(tag) + fixture.WordSize, value);

        Reject(bytes);
    }

    [Fact]
    public void Hash_tables_reject_oversized_counts_and_out_of_range_indices()
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        int hash = TagBlobOffset(fixture, bytes, 4);
        NativeElfTestImage.Write32(bytes, hash + 4, (uint)NativeElfReader.MaximumTableEntries + 1);
        Reject(bytes);
        bytes = fixture.Build();
        NativeElfTestImage.Write32(bytes, hash + 8, 2);
        Reject(bytes);
        bytes = fixture.Build();
        NativeElfTestImage.Write32(bytes, hash + 12, 2);
        Reject(bytes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Gnu_hash_tables_validate_buckets_and_bounded_chain_termination(int shape)
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        int hash = TagBlobOffset(fixture, bytes, 0x6ffffef5);
        if (shape == 2)
        {
            NativeElfTestImage.Write32(bytes, hash + 24, (uint)NativeElfReader.MaximumTableEntries);
        }
        else
        {
            NativeElfTestImage.Write32(bytes, hash + 28, 0);
            // End the file-backed load at this unterminated chain, not at a later unrelated table.
            NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 32, (ulong)hash + 32);
        }

        Reject(bytes);
    }

    [Fact]
    public void Dynamic_symbol_name_indices_cannot_escape_the_declared_string_table()
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        int symbols = TagBlobOffset(fixture, bytes, 6);
        NativeElfTestImage.Write32(bytes, symbols + 24, (uint)fixture.StringTableSize);

        Reject(bytes);
    }

    [Fact]
    public void Version_tables_validate_counts_links_string_offsets_and_versions()
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        int definitions = TagBlobOffset(fixture, bytes, 0x6ffffffc);
        NativeElfTestImage.Write16(bytes, definitions, 2);
        Reject(bytes);
        bytes = fixture.Build();
        NativeElfTestImage.Write32(bytes, definitions + 12, uint.MaxValue);
        Reject(bytes);
        bytes = fixture.Build();
        NativeElfTestImage.Write32(bytes, definitions + 20, (uint)fixture.StringTableSize);
        Reject(bytes);
        bytes = fixture.Build();
        int requirements = TagBlobOffset(fixture, bytes, 0x6ffffffe);
        NativeElfTestImage.Write16(bytes, requirements + 2, 0);
        Reject(bytes);
        bytes = fixture.Build();
        NativeElfTestImage.Write32(bytes, requirements + 12, 16);
        Reject(bytes);
    }

    [Theory]
    [InlineData(4UL, 0, 1_048_577U)]
    [InlineData(4UL, 4, 1_048_577U)]
    [InlineData(0x6ffffef5UL, 0, 1_048_577U)]
    [InlineData(0x6ffffef5UL, 4, 1_048_577U)]
    [InlineData(0x6ffffef5UL, 8, 1U << 21)]
    [InlineData(0x6ffffef5UL, 24, 1_048_576U)]
    [InlineData(0x6ffffef5UL, 28, 0U)]
    public void Hash_counts_prefixes_and_symbol_bounds_reject_fully_file_backed_oversized_tables_before_record_reads(
        ulong tag, int field, uint value)
    {
        var fixture = new NativeElfTestImage();
        byte[] hash = new byte[tag == 4 ? 8 : 32];
        NativeElfTestImage.Write32(hash, 0, 1);
        NativeElfTestImage.Write32(hash, 4, 1);
        if (tag != 4)
        {
            uint firstSymbol = field == 28 ? (uint)NativeElfReader.MaximumTableEntries - 1 : 1;
            NativeElfTestImage.Write32(hash, 4, firstSymbol);
            NativeElfTestImage.Write32(hash, 8, 1);
            NativeElfTestImage.Write32(hash, 12, 5);
            NativeElfTestImage.Write32(hash, 24, field == 4 ? 0 : firstSymbol);
            NativeElfTestImage.Write32(hash, 28, 1);
        }
        NativeElfTestImage.Write32(hash, field, value);
        fixture.AddTag(6, 0).AddTag(11, 24).AddTag(tag, fixture.AddBlob(hash));
        byte[] bytes = fixture.Build();
        // All declared ranges fit; missing cap checks would attempt sparse record reads.
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(6) + fixture.WordSize,
            fixture.BaseAddress + (ulong)bytes.Length);
        const long logicalLength = 1L << 42;
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 32, (ulong)logicalLength);
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 40, (ulong)logicalLength + fixture.BssBytes);
        using var stream = new MetadataStream(bytes, logicalLength);

        AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(stream)));
        Assert.Equal(0, stream.ReadsOutsideMetadata);
        Assert.InRange(stream.MaximumRequest, 1, 4096);
        Assert.InRange(stream.BytesRead, 1L, 16_384L);
    }

    [Theory]
    [InlineData(0x6ffffffcUL, 0x6ffffffdUL, true, false)]
    [InlineData(0x6ffffffeUL, 0x6fffffffUL, false, false)]
    [InlineData(0x6ffffffcUL, 0x6ffffffdUL, true, true)]
    [InlineData(0x6ffffffeUL, 0x6fffffffUL, false, true)]
    public void Version_record_and_auxiliary_counts_reject_fully_file_backed_oversized_tables_before_linked_reads(
        ulong addressTag, ulong countTag, bool definitions, bool auxiliaryCount)
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        int records = TagBlobOffset(fixture, bytes, addressTag);
        if (auxiliaryCount)
        {
            NativeElfTestImage.Write16(bytes, records + (definitions ? 6 : 2),
                (ushort)(NativeElfReader.MaximumDynamicEntries + 1));
            int auxiliary = records + (definitions ? 20 : 16);
            NativeElfTestImage.Write32(bytes, auxiliary + (definitions ? 4 : 12),
                (uint)(bytes.Length - auxiliary));
        }
        else
        {
            fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(countTag) + fixture.WordSize,
                (ulong)NativeElfReader.MaximumDynamicEntries + 1);
            NativeElfTestImage.Write32(bytes, records + (definitions ? 16 : 12), (uint)(bytes.Length - records));
        }
        // A traversal without the cap would follow the next link beyond fixture metadata.
        const long logicalLength = 1L << 42;
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 32, (ulong)logicalLength);
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 40, (ulong)logicalLength + fixture.BssBytes);
        using var stream = new MetadataStream(bytes, logicalLength);

        AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(stream)));
        Assert.Equal(0, stream.ReadsOutsideMetadata);
        Assert.InRange(stream.MaximumRequest, 1, 4096);
        Assert.InRange(stream.BytesRead, 1L, 16_384L);
    }

    [Theory]
    [InlineData(36UL, 35UL, 8)]
    [InlineData(25UL, 27UL, 8)]
    [InlineData(26UL, 28UL, 8)]
    [InlineData(32UL, 33UL, 8)]
    [InlineData(0x70000000UL, 0x70000001UL, 16)]
    public void Relr_array_and_executable_plt_ranges_reject_fully_file_backed_oversized_counts_before_record_reads(
        ulong addressTag, ulong sizeTag, int entrySize)
    {
        NativeElfTestImage fixture = MetadataShape();
        ulong tableSize = ((ulong)NativeElfReader.MaximumTableEntries + 1) * (ulong)entrySize;
        if (addressTag == 0x70000000)
        {
            fixture.AddTag(addressTag, 0).AddTag(sizeTag, tableSize).AddTag(0x70000003, (ulong)entrySize);
        }
        byte[] bytes = fixture.Build();
        long logicalLength = checked(bytes.LongLength + (long)tableSize);
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(addressTag) + fixture.WordSize,
            fixture.BaseAddress + (ulong)bytes.Length);
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(sizeTag) + fixture.WordSize, tableSize);
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 32, (ulong)logicalLength);
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 40, (ulong)logicalLength + fixture.BssBytes);
        using var stream = new MetadataStream(bytes, logicalLength);

        AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(stream)));
        Assert.Equal(0, stream.ReadsOutsideMetadata);
        Assert.InRange(stream.MaximumRequest, 1, 4096);
        Assert.InRange(stream.BytesRead, 1L, 16_384L);
    }

    [Theory]
    [InlineData(0, 0U)]
    [InlineData(4, 0U)]
    [InlineData(0, uint.MaxValue)]
    [InlineData(4, uint.MaxValue)]
    public void Sysv_hash_headers_require_positive_counts_and_a_fully_file_backed_range(int field, uint value)
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        NativeElfTestImage.Write32(bytes, TagBlobOffset(fixture, bytes, 4) + field, value);
        using var stream = new MetadataStream(bytes, bytes.LongLength);

        AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(stream)));
        Assert.Equal(0, stream.ReadsOutsideMetadata);
    }

    [Theory]
    [InlineData(0, 0U)]
    [InlineData(4, 0U)]
    [InlineData(8, 0U)]
    [InlineData(8, 3U)]
    [InlineData(12, 64U)]
    [InlineData(0, uint.MaxValue)]
    [InlineData(8, 0x80000000U)]
    public void Gnu_hash_headers_validate_positive_counts_bloom_shape_and_prefix_ranges(int field, uint value)
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        NativeElfTestImage.Write32(bytes, TagBlobOffset(fixture, bytes, 0x6ffffef5) + field, value);
        using var stream = new MetadataStream(bytes, bytes.LongLength);

        AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(stream)));
        Assert.Equal(0, stream.ReadsOutsideMetadata);
    }

    [Theory]
    [InlineData(6UL)]
    [InlineData(11UL)]
    public void Symbol_table_pointer_and_entry_width_tags_are_required_as_a_pair(ulong loneTag)
    {
        var fixture = new NativeElfTestImage();
        if (loneTag == 6)
        {
            fixture.AddTag(6, fixture.AddBlob(new byte[24]));
        }
        else
        {
            fixture.AddTag(11, 24);
        }

        Reject(fixture.Build());
    }

    [Theory]
    [InlineData(6UL)]
    [InlineData(0x6ffffff0UL)]
    [InlineData(34UL)]
    public void Symbol_derived_table_extents_must_be_fully_file_backed(ulong tag)
    {
        NativeElfTestImage fixture = MetadataShape();
        byte[] bytes = fixture.Build();
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(tag) + fixture.WordSize,
            fixture.BaseAddress + (ulong)bytes.Length - 1);
        using var stream = new MetadataStream(bytes, bytes.LongLength);

        AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(stream)));
        Assert.Equal(0, stream.ReadsOutsideMetadata);
    }

    [Theory]
    [InlineData(0x6ffffffcUL, 0x6ffffffdUL, 20)]
    [InlineData(0x6ffffffeUL, 0x6fffffffUL, 16)]
    public void Version_metadata_requires_positive_paired_counts_strings_and_a_file_backed_first_header(
        ulong addressTag, ulong countTag, int headerSize)
    {
        for (int shape = 0; shape < 5; shape++)
        {
            var fixture = new NativeElfTestImage { IncludeStringTable = shape != 4 };
            if (shape != 1)
            {
                fixture.AddTag(addressTag, fixture.AddBlob(new byte[headerSize]));
            }
            if (shape != 0)
            {
                fixture.AddTag(countTag, shape == 2 ? 0UL : 1UL);
            }
            byte[] bytes = fixture.Build();
            if (shape == 3)
            {
                fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(addressTag) + fixture.WordSize,
                    fixture.BaseAddress + (ulong)bytes.Length - 1);
            }
            using var stream = new MetadataStream(bytes, bytes.LongLength);

            AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(stream)));
            Assert.Equal(0, stream.ReadsOutsideMetadata);
        }
    }

    [Theory]
    [InlineData(7UL, 8UL, 24)]
    [InlineData(17UL, 18UL, 16)]
    [InlineData(23UL, 2UL, 24)]
    [InlineData(23UL, 2UL, 16)]
    public void Large_ordinary_relocation_ranges_including_Wireshark_rela_do_not_read_records(
        ulong addressTag, ulong sizeTag, int entrySize)
    {
        const ulong count = 1_846_573;
        NativeElfTestImage fixture = MetadataShape();
        ulong tableSize = count * (ulong)entrySize;
        byte[] bytes = fixture.Build();
        long logicalLength = checked(bytes.LongLength + (long)tableSize);
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(addressTag) + fixture.WordSize,
            fixture.BaseAddress + (ulong)bytes.Length);
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(sizeTag) + fixture.WordSize, tableSize);
        if (addressTag == 23)
        {
            fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(20) + fixture.WordSize, entrySize == 24 ? 7UL : 17UL);
        }
        if (addressTag == 7)
        {
            // Observed Wireshark ELF64 metadata: 44,317,752 bytes, including 1,836,258 relative entries.
            fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(0x6ffffff9) + fixture.WordSize, 1_836_258);
        }
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 32, (ulong)logicalLength);
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 40, (ulong)logicalLength + fixture.BssBytes);
        using var stream = new MetadataStream(bytes, logicalLength);

        NativeElfImage image = Assert.IsType<NativeElfImage>(NativeElfReader.Read(stream));

        Assert.True(image.HasSupportedAbi);
        Assert.Equal(["libc.so.6", "libpthread.so.0", "libdl.so.2"], image.Needed);
        Assert.Equal(0, stream.ReadsOutsideMetadata);
        Assert.InRange(stream.MaximumRequest, 1, 4096);
        Assert.InRange(stream.BytesRead, 1L, 16_384L);
    }

    [Theory]
    [InlineData("misaligned")]
    [InlineData("bss-tail")]
    [InlineData("truncated-file")]
    [InlineData("overlapping-load")]
    public void Large_rela_ranges_reject_misalignment_and_unbacked_or_ambiguous_extents_before_record_reads(string shape)
    {
        NativeElfTestImage fixture = MetadataShape();
        if (shape == "overlapping-load")
        {
            fixture.ExtraSegments.Add(new NativeElfTestImage.Segment(1, 0, 0, 24, 24, 4, 8));
        }
        byte[] bytes = fixture.Build();
        ulong tableSize = 1_846_573UL * 24 + (shape == "misaligned" ? 1UL : 0UL);
        long logicalLength = checked(bytes.LongLength + (long)tableSize);
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(7) + fixture.WordSize,
            fixture.BaseAddress + (ulong)bytes.Length);
        fixture.WriteAddress(bytes, fixture.DynamicEntryOffset(8) + fixture.WordSize, tableSize);
        ulong fileBackedLength = (ulong)logicalLength - (shape == "bss-tail" ? 24UL : 0UL);
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 32, fileBackedLength);
        NativeElfTestImage.Write64(bytes, fixture.ProgramHeaderOffset(0) + 40, (ulong)logicalLength + fixture.BssBytes);
        if (shape == "overlapping-load")
        {
            int overlap = fixture.ProgramHeaderOffset(2);
            ulong offset = (ulong)logicalLength - 24;
            NativeElfTestImage.Write64(bytes, overlap + 8, offset);
            NativeElfTestImage.Write64(bytes, overlap + 16, fixture.BaseAddress + offset);
            NativeElfTestImage.Write64(bytes, overlap + 24, fixture.BaseAddress + offset);
        }
        using var stream = new MetadataStream(bytes, logicalLength - (shape == "truncated-file" ? 24 : 0));

        AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(stream)));
        Assert.Equal(0, stream.ReadsOutsideMetadata);
        Assert.InRange(stream.MaximumRequest, 1, 4096);
        Assert.InRange(stream.BytesRead, 1L, 16_384L);
    }

    [Fact]
    public void Non_elf_is_the_only_null_result_and_the_reader_seeks_to_zero()
    {
        foreach (byte[] bytes in new byte[][] { [], [0x7f], [0x7f, (byte)'E', (byte)'L'], "not ELF"u8.ToArray() })
        {
            using var stream = new MemoryStream(bytes);
            stream.Position = stream.Length;
            Assert.Null(NativeElfReader.Read(stream));
        }
        byte[] elf = new NativeElfTestImage().Build();
        using var valid = new MemoryStream(elf);
        valid.Position = valid.Length;

        Assert.NotNull(NativeElfReader.Read(valid));
        Assert.True(valid.CanRead); // The descriptor owner, not the parser, disposes the stream.
    }

    [Fact]
    public void Short_reads_and_large_sparse_files_do_not_trigger_whole_image_reads()
    {
        var fixture = new NativeElfTestImage();
        fixture.AddStringTag(1, "libfixture.so");
        byte[] bytes = fixture.Build();
        using var stream = new MetadataStream(bytes, 1L << 42, maximumChunk: 3);
        stream.Position = 123;

        NativeElfImage image = Assert.IsType<NativeElfImage>(NativeElfReader.Read(stream));

        Assert.Equal(["libfixture.so"], image.Needed);
        Assert.InRange(stream.MaximumRequest, 1, 4096);
        Assert.True(stream.BytesRead < 4096);
    }

    [Fact]
    public void Stream_io_failures_use_the_same_redacted_failure_contract()
    {
        using var stream = new MetadataStream("\u007fELF"u8.ToArray(), 4, failReads: true);

        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(stream));

        AssertFailure(exception);
        Assert.DoesNotContain("synthetic-sensitive-path", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    private static NativeElfImage Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return Assert.IsType<NativeElfImage>(NativeElfReader.Read(stream));
    }

    private static OperationFailureException Reject(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => NativeElfReader.Read(stream));
        AssertFailure(exception);
        return exception;
    }

    private static NativeElfImage ParseModule(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return Assert.IsType<NativeElfImage>(NativeElfReader.ReadModuleMetadata(stream));
    }

    private static void RejectModule(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        AssertFailure(Assert.Throws<OperationFailureException>(() => NativeElfReader.ReadModuleMetadata(stream)));
    }

    private static void AssertFailure(OperationFailureException exception)
    {
        Assert.Equal(ExitCode.MissingPrerequisite, exception.Code);
        Assert.Equal("native-closure-unavailable", exception.Kind);
        Assert.Equal(NativeElfReader.Failure().Message, exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static int TagBlobOffset(NativeElfTestImage fixture, byte[] bytes, ulong tag)
    {
        int offset = fixture.DynamicEntryOffset(tag) + fixture.WordSize;
        ulong address = fixture.ElfClass == 1
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset))
            : BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
        return checked((int)(address - fixture.BaseAddress));
    }

    private static NativeElfTestImage MetadataShape(byte elfClass = 2, ushort machine = 62)
    {
        var fixture = new NativeElfTestImage(elfClass, machine: machine);
        ulong libc = fixture.AppendString("libc.so.6");
        ulong export = fixture.AppendString("fixture_export");
        ulong version = fixture.AppendString("GLIBC_FIXTURE_1.0");
        fixture.AddTag(1, libc).AddStringTag(1, "libpthread.so.0").AddStringTag(1, "libdl.so.2")
            .AddStringTag(14, "libcoreclr-fixture.so").AddStringTag(15, "/protected/rpath:$ORIGIN")
            .AddStringTag(29, "$ORIGIN:/protected/runtime").AddTag(30, 0x1f).AddTag(0x6ffffffb, 0x080008e9);
        int word = fixture.WordSize;
        int symbolSize = elfClass == 1 ? 16 : 24;
        byte[] symbols = new byte[symbolSize * 2];
        NativeElfTestImage.Write32(symbols, symbolSize, (uint)export);
        fixture.AddTag(6, fixture.AddBlob(symbols)).AddTag(11, (ulong)symbolSize);
        byte[] hash = new byte[20];
        NativeElfTestImage.Write32(hash, 0, 1);
        NativeElfTestImage.Write32(hash, 4, 2);
        NativeElfTestImage.Write32(hash, 8, 1);
        fixture.AddTag(4, fixture.AddBlob(hash));
        byte[] gnuHash = new byte[16 + word + 8];
        NativeElfTestImage.Write32(gnuHash, 0, 1);
        NativeElfTestImage.Write32(gnuHash, 4, 1);
        NativeElfTestImage.Write32(gnuHash, 8, 1);
        NativeElfTestImage.Write32(gnuHash, 12, 5);
        NativeElfTestImage.Write32(gnuHash, 16 + word, 1);
        NativeElfTestImage.Write32(gnuHash, 20 + word, 1);
        fixture.AddTag(0x6ffffef5, fixture.AddBlob(gnuHash));
        fixture.AddTag(0x6ffffff0, fixture.AddBlob([0, 0, 2, 0]));
        fixture.AddTag(34, fixture.AddBlob(new byte[8]));

        ulong target = fixture.BaseAddress + (ulong)fixture.HeaderSize;
        ulong relative = machine == 183 ? 1027UL : machine == 40 ? 23UL : 8UL;
        byte[] rel = new byte[word * 2];
        fixture.WriteAddress(rel, 0, target);
        fixture.WriteAddress(rel, word, relative);
        NativeElfTestImage.Blob relBlob = fixture.AddBlob(rel);
        fixture.AddTag(17, relBlob).AddTag(18, (ulong)rel.Length).AddTag(19, (ulong)rel.Length).AddTag(0x6ffffffa, 1);
        byte[] rela = new byte[word * 3];
        fixture.WriteAddress(rela, 0, target);
        fixture.WriteAddress(rela, word, relative);
        NativeElfTestImage.Blob relaBlob = fixture.AddBlob(rela);
        fixture.AddTag(7, relaBlob).AddTag(8, (ulong)rela.Length).AddTag(9, (ulong)rela.Length).AddTag(0x6ffffff9, 1);
        byte[] relr = new byte[word];
        fixture.WriteAddress(relr, 0, target);
        fixture.AddTag(36, fixture.AddBlob(relr)).AddTag(35, (ulong)word).AddTag(37, (ulong)word);
        fixture.AddTag(23, elfClass == 1 ? relBlob : relaBlob).AddTag(2, (ulong)(elfClass == 1 ? rel.Length : rela.Length))
            .AddTag(20, elfClass == 1 ? 17UL : 7UL);
        NativeElfTestImage.Blob array = fixture.AddBlob(new byte[word]);
        fixture.AddTag(25, array).AddTag(27, (ulong)word).AddTag(26, array).AddTag(28, (ulong)word)
            .AddTag(32, array).AddTag(33, (ulong)word).AddTag(3, array).AddTag(12, target).AddTag(13, target)
            .AddTag(0x6ffffef6, target).AddTag(0x6ffffef7, array);
        fixture.AddTag(16, 0).AddTag(21, 0).AddTag(22, 0).AddTag(24, 0)
            .AddTag(0x6ffffdf5, 1).AddTag(0x6ffffdf8, 1);
        byte[] definition = new byte[28];
        NativeElfTestImage.Write16(definition, 0, 1);
        NativeElfTestImage.Write16(definition, 4, 2);
        NativeElfTestImage.Write16(definition, 6, 1);
        NativeElfTestImage.Write32(definition, 12, 20);
        NativeElfTestImage.Write32(definition, 20, (uint)version);
        fixture.AddTag(0x6ffffffc, fixture.AddBlob(definition)).AddTag(0x6ffffffd, 1);
        byte[] requirement = new byte[32];
        NativeElfTestImage.Write16(requirement, 0, 1);
        NativeElfTestImage.Write16(requirement, 2, 1);
        NativeElfTestImage.Write32(requirement, 4, (uint)libc);
        NativeElfTestImage.Write32(requirement, 8, 16);
        NativeElfTestImage.Write16(requirement, 22, 2);
        NativeElfTestImage.Write32(requirement, 24, (uint)version);
        fixture.AddTag(0x6ffffffe, fixture.AddBlob(requirement)).AddTag(0x6fffffff, 1);
        if (machine == 183)
        {
            fixture.AddTag(0x70000001, 0).AddTag(0x70000003, 0).AddTag(0x70000005, 0);
        }
        return fixture;
    }

    private sealed class MetadataStream(byte[] bytes, long logicalLength, int maximumChunk = int.MaxValue,
        bool failReads = false, bool canSeek = true, bool canRead = true) : Stream
    {
        private long position;
        internal long BytesRead { get; private set; }
        internal int MaximumRequest { get; private set; }
        internal int ReadsOutsideMetadata { get; private set; }
        public override bool CanRead => canRead;
        public override bool CanSeek => canSeek;
        public override bool CanWrite => false;
        public override long Length => logicalLength;
        public override long Position { get => position; set => position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (failReads)
            {
                throw new IOException("synthetic-sensitive-path");
            }
            MaximumRequest = Math.Max(MaximumRequest, buffer.Length);
            if (position < 0 || position >= bytes.Length)
            {
                ReadsOutsideMetadata++;
                throw new IOException("The parser read outside fixture metadata.");
            }
            int count = Math.Min(Math.Min(buffer.Length, maximumChunk), bytes.Length - (int)position);
            bytes.AsSpan((int)position, count).CopyTo(buffer);
            position += count;
            BytesRead += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(logicalLength + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return Position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
