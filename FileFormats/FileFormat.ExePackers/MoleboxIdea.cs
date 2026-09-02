#pragma warning disable CS1591
namespace FileFormat.ExePackers;

/// <summary>
/// The IDEA block cipher (Lai/Massey, "IPES", 1991) in the shape MoleBox uses
/// it: 64-bit blocks, a 128-bit key, ECB.
/// </summary>
/// <remarks>
/// <para>
/// Implemented from the published description of the algorithm, not from any
/// implementation: eight rounds over four 16-bit words, each round mixing with
/// multiplication modulo 2^16+1 (where a zero operand means 2^16 and a product
/// of 2^16 means zero), addition modulo 2^16, and exclusive-or, followed by the
/// half-round output transform.
/// </para>
/// <para>
/// The encryption key schedule is the documented one: the 128-bit key yields
/// the first eight 16-bit subkeys, then the key is rotated left 25 bits for the
/// next eight, and so on until 52 subkeys exist. The decryption schedule is the
/// documented inversion of that: the rounds run backwards with multiplicative
/// inverses modulo 2^16+1 and additive inverses modulo 2^16, and the two inner
/// addition subkeys swap places in every round but the first and last.
/// </para>
/// </remarks>
public static class MoleboxIdea {
  private const int Modulus = 0x10001;
  /// <summary>
  /// Defines the block size constant value.
  /// </summary>
public const int BlockSize = 8;
  private const int SubkeyCount = 52;

  /// <summary>Builds the 52 encryption subkeys from a 16-byte key.</summary>
  public static ushort[] ExpandKey(ReadOnlySpan<byte> key) {
    ArgumentOutOfRangeException.ThrowIfLessThan(key.Length, 16, nameof(key));

    var subkeys = new ushort[SubkeyCount];
    // The key is treated as one 128-bit big-endian value that rotates left by
    // 25 bits every time another eight subkeys are needed.
    Span<byte> state = stackalloc byte[16];
    key[..16].CopyTo(state);
    for (var produced = 0; produced < SubkeyCount;) {
      for (var word = 0; word < 8 && produced < SubkeyCount; ++word, ++produced)
        subkeys[produced] = (ushort)((state[2 * word] << 8) | state[2 * word + 1]);
      RotateLeft25(state);
    }
    return subkeys;
  }

  /// <summary>Turns an encryption schedule into the matching decryption one.</summary>
  public static ushort[] InvertKey(ReadOnlySpan<ushort> encryption) {
    var decryption = new ushort[SubkeyCount];
    var write = SubkeyCount;
    var read = 0;

    // Last round first: its four subkeys become the new first four, with the
    // multiplicative ones inverted and the additive ones negated.
    write -= 4;
    decryption[write + 0] = MultiplicativeInverse(encryption[read + 0]);
    decryption[write + 1] = AdditiveInverse(encryption[read + 1]);
    decryption[write + 2] = AdditiveInverse(encryption[read + 2]);
    decryption[write + 3] = MultiplicativeInverse(encryption[read + 3]);
    read += 4;

    for (var round = 1; round < 8; ++round) {
      write -= 2;
      decryption[write + 0] = encryption[read + 0];
      decryption[write + 1] = encryption[read + 1];
      read += 2;

      write -= 4;
      decryption[write + 0] = MultiplicativeInverse(encryption[read + 0]);
      // The two addition subkeys change places in every middle round.
      decryption[write + 1] = AdditiveInverse(encryption[read + 2]);
      decryption[write + 2] = AdditiveInverse(encryption[read + 1]);
      decryption[write + 3] = MultiplicativeInverse(encryption[read + 3]);
      read += 4;
    }

    write -= 2;
    decryption[write + 0] = encryption[read + 0];
    decryption[write + 1] = encryption[read + 1];
    read += 2;

    write -= 4;
    decryption[write + 0] = MultiplicativeInverse(encryption[read + 0]);
    decryption[write + 1] = AdditiveInverse(encryption[read + 1]);
    decryption[write + 2] = AdditiveInverse(encryption[read + 2]);
    decryption[write + 3] = MultiplicativeInverse(encryption[read + 3]);
    return decryption;
  }

  /// <summary>Runs one 8-byte block through the cipher with the given schedule.</summary>
  public static void ProcessBlock(ReadOnlySpan<byte> input, Span<byte> output, ReadOnlySpan<ushort> subkeys) {
    var x1 = (ushort)((input[0] << 8) | input[1]);
    var x2 = (ushort)((input[2] << 8) | input[3]);
    var x3 = (ushort)((input[4] << 8) | input[5]);
    var x4 = (ushort)((input[6] << 8) | input[7]);

    var k = 0;
    for (var round = 0; round < 8; ++round) {
      x1 = Multiply(x1, subkeys[k++]);
      x2 = (ushort)(x2 + subkeys[k++]);
      x3 = (ushort)(x3 + subkeys[k++]);
      x4 = Multiply(x4, subkeys[k++]);

      var t2 = Multiply((ushort)(x1 ^ x3), subkeys[k++]);
      var t1 = Multiply((ushort)(t2 + (x2 ^ x4)), subkeys[k++]);
      t2 = (ushort)(t1 + t2);

      x1 ^= t1;
      x4 ^= t2;
      t2 ^= x2;
      x2 = (ushort)(x3 ^ t1);
      x3 = t2;
    }

    var y1 = Multiply(x1, subkeys[k++]);
    var y2 = (ushort)(x3 + subkeys[k++]);
    var y3 = (ushort)(x2 + subkeys[k++]);
    var y4 = Multiply(x4, subkeys[k]);

    output[0] = (byte)(y1 >> 8);
    output[1] = (byte)y1;
    output[2] = (byte)(y2 >> 8);
    output[3] = (byte)y2;
    output[4] = (byte)(y3 >> 8);
    output[5] = (byte)y3;
    output[6] = (byte)(y4 >> 8);
    output[7] = (byte)y4;
  }

  /// <summary>Runs a whole buffer through the cipher in ECB mode; a trailing partial block is left alone.</summary>
  public static byte[] ProcessEcb(ReadOnlySpan<byte> data, ReadOnlySpan<ushort> subkeys) {
    var result = data.ToArray();
    for (var offset = 0; offset + BlockSize <= result.Length; offset += BlockSize)
      ProcessBlock(result.AsSpan(offset, BlockSize), result.AsSpan(offset, BlockSize), subkeys);
    return result;
  }

  private static void RotateLeft25(Span<byte> state) {
    // 25 bits = three whole bytes plus one bit.
    Span<byte> rotated = stackalloc byte[16];
    for (var i = 0; i < 16; ++i) {
      var high = state[(i + 3) % 16];
      var low = state[(i + 4) % 16];
      rotated[i] = (byte)((high << 1) | (low >> 7));
    }
    rotated.CopyTo(state);
  }

  /// <summary>Multiplication modulo 2^16+1 with zero standing in for 2^16.</summary>
  private static ushort Multiply(ushort a, ushort b) {
    if (a == 0)
      return (ushort)(Modulus - b);
    if (b == 0)
      return (ushort)(Modulus - a);
    // Unsigned: the widest product is 0xFFFF * 0xFFFF, which overflows a signed
    // 32-bit int.
    var product = (uint)a * b % Modulus;
    return (ushort)product;
  }

  private static ushort AdditiveInverse(ushort value) => (ushort)-value;

  private static ushort MultiplicativeInverse(ushort value) {
    if (value <= 1)
      return value;

    // Extended Euclid over 2^16+1; the modulus is prime, so every non-zero
    // residue has an inverse.
    int previous = 0, current = 1, remainder = Modulus, next = value;
    while (next != 0) {
      var quotient = remainder / next;
      (previous, current) = (current, previous - quotient * current);
      (remainder, next) = (next, remainder - quotient * next);
    }
    return (ushort)(previous < 0 ? previous + Modulus : previous);
  }
}
