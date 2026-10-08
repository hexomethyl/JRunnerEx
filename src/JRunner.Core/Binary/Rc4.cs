using System.Security.Cryptography;

namespace JRunner.Core.Binary;

/// <summary>
/// Implements the legacy RC4 stream transform used by Xbox binary formats.
/// </summary>
/// <remarks>
/// RC4 is retained solely for compatibility with existing image formats; it is not suitable for
/// new security-sensitive protocol designs.
/// </remarks>
public static class Rc4
{
    private const int StateLength = 256;
    private const int CancellationCheckIntervalMask = 0x0FFF;

    /// <summary>
    /// Applies the RC4 transform to <paramref name="input"/> and returns a separately allocated result.
    /// </summary>
    /// <param name="key">The nonempty RC4 key.</param>
    /// <param name="input">The plaintext or ciphertext to transform.</param>
    /// <returns>A transformed copy of <paramref name="input"/>.</returns>
    public static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input)
    {
        return Transform(key, input, CancellationToken.None);
    }

    /// <summary>
    /// Applies the RC4 transform to <paramref name="input"/> and returns a separately allocated result.
    /// </summary>
    /// <param name="key">The nonempty RC4 key.</param>
    /// <param name="input">The plaintext or ciphertext to transform.</param>
    /// <param name="cancellationToken">Cancels a large transform before a result is returned.</param>
    /// <returns>A transformed copy of <paramref name="input"/>.</returns>
    public static byte[] Transform(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> input,
        CancellationToken cancellationToken)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (input.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        byte[] output = GC.AllocateUninitializedArray<byte>(input.Length);
        input.CopyTo(output);

        bool completed = false;
        try
        {
            TransformInPlaceCore(key, output, cancellationToken);
            completed = true;
            return output;
        }
        finally
        {
            if (!completed)
            {
                CryptographicOperations.ZeroMemory(output);
            }
        }
    }

    /// <summary>
    /// Applies the RC4 transform directly to <paramref name="buffer"/>.
    /// </summary>
    /// <param name="key">The nonempty RC4 key.</param>
    /// <param name="buffer">The plaintext or ciphertext to transform in place.</param>
    public static void TransformInPlace(ReadOnlySpan<byte> key, Span<byte> buffer)
    {
        TransformInPlace(key, buffer, CancellationToken.None);
    }

    /// <summary>
    /// Applies the RC4 transform directly to <paramref name="buffer"/>.
    /// </summary>
    /// <param name="key">The nonempty RC4 key.</param>
    /// <param name="buffer">The plaintext or ciphertext to transform in place.</param>
    /// <param name="cancellationToken">Cancels a large transform. The buffer can be partially transformed when cancelled.</param>
    public static void TransformInPlace(
        ReadOnlySpan<byte> key,
        Span<byte> buffer,
        CancellationToken cancellationToken)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        TransformInPlaceCore(key, buffer, cancellationToken);
    }

    private static void TransformInPlaceCore(
        ReadOnlySpan<byte> key,
        Span<byte> buffer,
        CancellationToken cancellationToken)
    {
        Span<byte> state = stackalloc byte[StateLength];

        try
        {
            for (int index = 0; index < state.Length; index++)
            {
                state[index] = (byte)index;
            }

            int keyIndex = 0;
            for (int index = 0; index < state.Length; index++)
            {
                keyIndex = (keyIndex + state[index] + key[index % key.Length]) & 0xFF;
                Swap(state, index, keyIndex);
            }

            int i = 0;
            int j = 0;
            for (int index = 0; index < buffer.Length; index++)
            {
                if ((index & CancellationCheckIntervalMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                i = (i + 1) & 0xFF;
                j = (j + state[i]) & 0xFF;
                Swap(state, i, j);
                buffer[index] ^= state[(state[i] + state[j]) & 0xFF];
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(state);
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("An RC4 key must contain at least one byte.", nameof(key));
        }
    }

    private static void Swap(Span<byte> values, int left, int right)
    {
        byte value = values[left];
        values[left] = values[right];
        values[right] = value;
    }
}
