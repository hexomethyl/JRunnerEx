using System.Text;
using JRunner.Core.Binary;
using Xunit;

namespace JRunner.Core.Tests.Binary;

public sealed class Crc32Tests
{
    [Fact]
    public void Compute_span_matches_standard_crc32_test_vector()
    {
        byte[] data = Encoding.ASCII.GetBytes("123456789");

        uint checksum = Crc32.Compute(data);

        Assert.Equal(0xCBF43926u, checksum);
    }

    [Fact]
    public void Compute_segments_matches_the_standard_crc32_test_vector()
    {
        uint checksum = Crc32.Compute(
            Encoding.ASCII.GetBytes("123"),
            Encoding.ASCII.GetBytes("456"),
            Encoding.ASCII.GetBytes("789"));

        Assert.Equal(0xCBF43926u, checksum);
    }

    [Fact]
    public void Compute_seekable_stream_hashes_its_full_contents_and_preserves_position()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("123456789"));
        stream.Position = 4;

        uint checksum = Crc32.Compute(stream);

        Assert.Equal(0xCBF43926u, checksum);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void Compute_stream_range_rejects_out_of_bounds_requests_without_moving_position()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("123456789"));
        stream.Position = 2;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            Crc32.Compute(stream, 7, 3);
        });

        Assert.Equal(2, stream.Position);
    }
}
