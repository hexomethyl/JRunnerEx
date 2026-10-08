using System.Text;
using JRunner.Core.Binary;
using Xunit;

namespace JRunner.Core.Tests.Binary;

public sealed class XeCryptTests
{
    [Fact]
    public void Hmac_sha1_truncated_matches_rfc_2202_test_case_one()
    {
        byte[] key = new byte[20];
        Array.Fill(key, (byte)0x0B);
        byte[] message = Encoding.ASCII.GetBytes("Hi There");
        byte[] expectedTag = Convert.FromHexString("B617318655057264E28BC0B6FB378C8E");

        byte[] actualTag = XeCrypt.HmacSha1Truncated(key, message);

        Assert.Equal(expectedTag, actualTag);
    }

    [Fact]
    public void Hmac_sha1_truncated_multiple_segments_matches_the_same_rfc_2202_vector()
    {
        byte[] key = new byte[20];
        Array.Fill(key, (byte)0x0B);
        byte[] expectedTag = Convert.FromHexString("B617318655057264E28BC0B6FB378C8E");
        var segments = new ReadOnlyMemory<byte>[]
        {
            Encoding.ASCII.GetBytes("Hi "),
            Encoding.ASCII.GetBytes("There"),
        };

        byte[] actualTag = XeCrypt.HmacSha1Truncated(key, segments);
        byte[] contiguousTag = XeCrypt.HmacSha1Truncated(key, Encoding.ASCII.GetBytes("Hi There"));

        Assert.Equal(expectedTag, actualTag);
        Assert.Equal(contiguousTag, actualTag);
    }

    [Fact]
    public void Hmac_sha1_truncated_requires_an_exactly_sized_destination()
    {
        byte[] key = new byte[20];
        Array.Fill(key, (byte)0x0B);

        Assert.Throws<ArgumentException>(() =>
        {
            XeCrypt.HmacSha1Truncated(key, Encoding.ASCII.GetBytes("Hi There"), new byte[15]);
        });
    }

    [Fact]
    public void Fixed_time_equals_reports_equal_and_unequal_tags()
    {
        byte[] tag = Convert.FromHexString("B617318655057264E28BC0B6FB378C8E");
        byte[] changedTag = tag.ToArray();
        changedTag[^1] ^= 0x01;

        Assert.True(XeCrypt.FixedTimeEquals(tag, tag.ToArray()));
        Assert.False(XeCrypt.FixedTimeEquals(tag, changedTag));
        Assert.False(XeCrypt.FixedTimeEquals(tag, tag[..^1]));
    }
}
