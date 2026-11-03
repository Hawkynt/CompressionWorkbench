using System.Buffers.Binary;

namespace Compression.Core.Crypto;

/// <summary>
/// The Serpent block cipher (Ross Anderson, Eli Biham, Lars Knudsen), 128-bit blocks and keys of
/// up to 256 bits, in its bitslice description with little-endian 32-bit words — the byte order of
/// the NESSIE test vectors.
/// </summary>
/// <remarks>
/// Written from "Serpent: A Proposal for the Advanced Encryption Standard" (the bitslice
/// description, section 2): 32 rounds of key mixing, one of eight 4-bit S-boxes applied across
/// the four words, and the linear transformation; a shorter key is padded with a single 1 bit. The
/// S-box tables are the paper's. Only encryption is provided: the counter and feedback modes that
/// use Serpent need no other direction.
/// </remarks>
public sealed class Serpent {
  private const int Rounds = 32;
  private const uint Phi = 0x9E3779B9;

  private static readonly byte[][] SBoxes = [
    [3, 8, 15, 1, 10, 6, 5, 11, 14, 13, 4, 2, 7, 0, 9, 12],
    [15, 12, 2, 7, 9, 0, 5, 10, 1, 11, 14, 8, 6, 13, 3, 4],
    [8, 6, 7, 9, 3, 12, 10, 15, 13, 1, 14, 4, 0, 11, 5, 2],
    [0, 15, 11, 8, 12, 9, 6, 3, 13, 1, 2, 4, 10, 7, 5, 14],
    [1, 15, 8, 3, 12, 0, 11, 6, 2, 5, 4, 10, 9, 14, 7, 13],
    [15, 5, 2, 11, 4, 10, 9, 12, 0, 3, 14, 8, 13, 6, 7, 1],
    [7, 2, 12, 5, 8, 4, 6, 11, 14, 9, 1, 15, 13, 3, 10, 0],
    [1, 13, 15, 0, 14, 8, 2, 11, 7, 4, 12, 10, 9, 3, 5, 6],
  ];

  private readonly uint[] _subkeys = new uint[(Rounds + 1) * 4];

  /// <summary>Expands <paramref name="key"/> (1 to 32 bytes) into the 33 round keys.</summary>
  /// <exception cref="ArgumentException">The key is empty or longer than 32 bytes.</exception>
  public Serpent(ReadOnlySpan<byte> key) {
    if (key.Length is < 1 or > 32)
      throw new ArgumentException("Serpent keys are 1 to 32 bytes.", nameof(key));

    Span<byte> padded = stackalloc byte[32];
    key.CopyTo(padded);
    if (key.Length < 32)
      padded[key.Length] = 1;

    var w = new uint[8 + this._subkeys.Length];
    for (var i = 0; i < 8; ++i)
      w[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded[(4 * i)..]);
    for (var i = 8; i < w.Length; ++i)
      w[i] = uint.RotateLeft(w[i - 8] ^ w[i - 5] ^ w[i - 3] ^ w[i - 1] ^ Phi ^ (uint)(i - 8), 11);

    for (var k = 0; k <= Rounds; ++k) {
      uint a = w[8 + 4 * k], b = w[9 + 4 * k], c = w[10 + 4 * k], d = w[11 + 4 * k];
      ApplySBox(SBoxes[(3 - k) & 7], ref a, ref b, ref c, ref d);
      this._subkeys[4 * k] = a;
      this._subkeys[4 * k + 1] = b;
      this._subkeys[4 * k + 2] = c;
      this._subkeys[4 * k + 3] = d;
    }
  }

  /// <summary>Encrypts one 16-byte block in place.</summary>
  public void Encrypt(Span<byte> block) {
    if (block.Length != 16)
      throw new ArgumentException("Serpent blocks are 16 bytes.", nameof(block));
    var x0 = BinaryPrimitives.ReadUInt32LittleEndian(block);
    var x1 = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
    var x2 = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
    var x3 = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
    var k = this._subkeys;
    for (var r = 0; r < Rounds; ++r) {
      x0 ^= k[4 * r];
      x1 ^= k[4 * r + 1];
      x2 ^= k[4 * r + 2];
      x3 ^= k[4 * r + 3];
      ApplySBox(SBoxes[r & 7], ref x0, ref x1, ref x2, ref x3);
      if (r == Rounds - 1)
        break;
      x0 = uint.RotateLeft(x0, 13);
      x2 = uint.RotateLeft(x2, 3);
      x1 ^= x0 ^ x2;
      x3 ^= x2 ^ (x0 << 3);
      x1 = uint.RotateLeft(x1, 1);
      x3 = uint.RotateLeft(x3, 7);
      x0 ^= x1 ^ x3;
      x2 ^= x3 ^ (x1 << 7);
      x0 = uint.RotateLeft(x0, 5);
      x2 = uint.RotateLeft(x2, 22);
    }
    BinaryPrimitives.WriteUInt32LittleEndian(block, x0 ^ k[4 * Rounds]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[4..], x1 ^ k[4 * Rounds + 1]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[8..], x2 ^ k[4 * Rounds + 2]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[12..], x3 ^ k[4 * Rounds + 3]);
  }

  /// <summary>Applies a 4-bit S-box to each of the 32 bit columns of the four words (word 0 holds the low bit).</summary>
  private static void ApplySBox(byte[] box, ref uint x0, ref uint x1, ref uint x2, ref uint x3) {
    uint y0 = 0, y1 = 0, y2 = 0, y3 = 0;
    for (var bit = 0; bit < 32; ++bit) {
      var nibble = ((x0 >> bit) & 1) | (((x1 >> bit) & 1) << 1) | (((x2 >> bit) & 1) << 2) | (((x3 >> bit) & 1) << 3);
      var s = (uint)box[nibble];
      y0 |= (s & 1) << bit;
      y1 |= ((s >> 1) & 1) << bit;
      y2 |= ((s >> 2) & 1) << bit;
      y3 |= ((s >> 3) & 1) << bit;
    }
    (x0, x1, x2, x3) = (y0, y1, y2, y3);
  }
}
