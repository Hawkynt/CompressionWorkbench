using System.Text;

namespace FileFormat.Daa;

/// <summary>
/// The nibble-permutation scheme of password-protected DAA images, implemented from
/// <c>docs/DAA-ON-DISK.md</c> ("Password-protected images"): the password yields one nibble
/// permutation per block size from 1 to 128 bytes, and stored chunks are restored block by block.
/// </summary>
internal sealed class DaaCipher {
  /// <summary>Bytes per full block; a shorter tail uses the permutation of its own length.</summary>
  public const int BlockSize = 128;

  private readonly byte[][] _permutations = new byte[BlockSize + 1][];

  /// <summary>Derives the permutations from <paramref name="password"/> (Latin-1 bytes; ASCII in practice).</summary>
  public DaaCipher(string password) {
    var bytes = Encoding.Latin1.GetBytes(password);
    for (var n = 1; n <= BlockSize; ++n)
      this._permutations[n] = Permutation(bytes, 2 * n);
  }

  /// <summary>Whether the 128-byte key of the password descriptor, restored, has the check CRC.</summary>
  public bool Accepts(ReadOnlySpan<byte> key, uint checkCrc) {
    Span<byte> restored = stackalloc byte[BlockSize];
    Restore(key[..BlockSize], restored, this._permutations[BlockSize]);
    return DaaImage.Crc32(restored) == checkCrc;
  }

  /// <summary>Restores stored chunk bytes in place: full 128-byte blocks, then the tail.</summary>
  public void Restore(Span<byte> data) {
    Span<byte> block = stackalloc byte[BlockSize];
    for (var at = 0; at < data.Length; at += BlockSize) {
      var n = Math.Min(BlockSize, data.Length - at);
      var source = data.Slice(at, n);
      Restore(source, block[..n], this._permutations[n]);
      block[..n].CopyTo(source);
    }
  }

  // Nibble k of the input (low nibble of byte k/2 for even k, high for odd) moves to position
  // table[k]: byte table[k]/2 of the output, its high nibble when table[k] is odd.
  private static void Restore(ReadOnlySpan<byte> input, Span<byte> output, byte[] table) {
    output.Clear();
    for (var k = 0; k < 2 * input.Length; ++k) {
      var nibble = (k & 1) == 0 ? input[k >> 1] & 0x0F : input[k >> 1] >> 4;
      var target = table[k];
      output[target >> 1] |= (byte)((target & 1) != 0 ? nibble << 4 : nibble);
    }
  }

  /// <summary>The permutation of <paramref name="positions"/> nibble positions (spec: "Building Tₙ").</summary>
  private static byte[] Permutation(ReadOnlySpan<byte> password, int positions) {
    var table = new byte[positions];
    var taken = new bool[positions];
    var first = password.IsEmpty ? 0 : password[0];

    // Moves the cursor to the steps-th position not yet taken, wrapping at the end to restart.
    int Step(int cursor, int steps, int restart) {
      for (var counted = 0; counted < steps;) {
        if (++cursor == positions)
          cursor = restart;
        if (!taken[cursor])
          ++counted;
      }
      return cursor;
    }

    void Take(int index, int position) {
      table[index] = (byte)position;
      taken[position] = true;
    }

    if (positions <= 64) {
      var cursor = Math.Min(first >> 5, positions - 1);
      for (var index = 0; index < positions; ++index)
        Take(index, cursor = Step(cursor, 11, 0));
      return table;
    }

    var at = first >> 5;
    Take(0, at + 32);
    var next = 1;
    var filled = 1;
    for (; filled < positions - 32; ++filled) {
      var steps = next < password.Length && password[next] != 0 ? password[next] : 11;
      if (next < password.Length)
        ++next;
      Take(filled, at = Step(at, steps, 32));
    }
    var tailSteps = (first & 7) == 0 ? 7 : first & 7;
    for (; filled < positions; ++filled)
      Take(filled, at = Step(at, tailSteps, 0));

    var shuffle = first & 24;
    if (shuffle == 0)
      return table;
    var order = (byte[])table.Clone();
    Array.Clear(taken);
    var walk = 0;
    for (var index = 0; index < positions; ++index) {
      walk = Step(walk, shuffle, 0);
      table[index] = order[walk];
      taken[walk] = true;
    }
    return table;
  }
}
