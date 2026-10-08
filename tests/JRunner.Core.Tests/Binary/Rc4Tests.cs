using System.Text;
using JRunner.Core.Binary;
using Xunit;

namespace JRunner.Core.Tests.Binary;

public sealed class Rc4Tests
{
    [Fact]
    public void Transform_matches_the_published_rc4_key_plaintext_vector()
    {
        byte[] key = Encoding.ASCII.GetBytes("Key");
        byte[] plaintext = Encoding.ASCII.GetBytes("Plaintext");
        byte[] expectedCiphertext = Convert.FromHexString("BBF316E8D940AF0AD3");

        byte[] ciphertext = Rc4.Transform(key, plaintext);

        Assert.Equal(expectedCiphertext, ciphertext);
        Assert.Equal(plaintext, Rc4.Transform(key, ciphertext));
    }

    [Fact]
    public void Transform_in_place_matches_the_published_rc4_key_plaintext_vector()
    {
        byte[] key = Encoding.ASCII.GetBytes("Key");
        byte[] buffer = Encoding.ASCII.GetBytes("Plaintext");
        byte[] expectedCiphertext = Convert.FromHexString("BBF316E8D940AF0AD3");

        Rc4.TransformInPlace(key, buffer);

        Assert.Equal(expectedCiphertext, buffer);
    }

    [Fact]
    public void Transform_rejects_an_empty_key()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            Rc4.Transform(Array.Empty<byte>(), new byte[] { 0x00 });
        });
    }
}
