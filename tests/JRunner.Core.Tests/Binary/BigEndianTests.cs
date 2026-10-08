using JRunner.Core.Binary;
using Xunit;

namespace JRunner.Core.Tests.Binary;

public sealed class BigEndianTests
{
    [Fact]
    public void Read_uint16_and_uint32_decode_big_endian_fields_at_offsets()
    {
        byte[] source = { 0x00, 0x12, 0x34, 0x56, 0x78 };

        Assert.Equal((ushort)0x1234, BigEndian.ReadUInt16(source, 1));
        Assert.Equal(0x12345678u, BigEndian.ReadUInt32(source, 1));
    }

    [Fact]
    public void Write_uint16_and_uint32_encode_big_endian_fields_at_offsets()
    {
        byte[] destination = new byte[6];

        BigEndian.WriteUInt16(destination, 0, 0x1234);
        BigEndian.WriteUInt32(destination, 2, 0x89ABCDEFu);

        Assert.Equal(Convert.FromHexString("123489ABCDEF"), destination);
    }

    [Fact]
    public void Slice_exact_returns_only_the_requested_bytes()
    {
        byte[] source = { 0xA0, 0xB1, 0xC2, 0xD3 };

        ReadOnlySpan<byte> slice = BigEndian.SliceExact(source, 1, 2);

        Assert.Equal(Convert.FromHexString("B1C2"), slice.ToArray());
    }

    [Fact]
    public void Read_write_and_slice_reject_out_of_bounds_fields()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            BigEndian.ReadUInt32(new byte[3]);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            BigEndian.WriteUInt16(new byte[1], 0x1234);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            BigEndian.SliceExact(new byte[4], 3, 2);
        });
    }
}
