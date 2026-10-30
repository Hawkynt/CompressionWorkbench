using System.Buffers.Binary;
using System.Numerics;

namespace Compression.Core.Crypto;

/// <summary>
/// The RAR 2.0 file cipher: a 32-round, 128-bit-block Feistel network whose round function is a
/// password-permuted byte substitution, with keys that absorb CRC-32 table words of every ciphertext
/// block. Used by RAR 2.x archives (unpack versions 20 to 28); RAR 2.9 and later use AES.
/// </summary>
/// <remarks>
/// Converted from 7-Zip's <c>CPP/7zip/Crypto/Rar20Crypto.cpp</c> (Igor Pavlov, LGPL-2.1-or-later); the
/// initial substitution table and key constants are copied from it verbatim. Each file of an archive
/// starts from a fresh <see cref="Rar20Cipher"/> keyed with the password; data is processed in whole
/// 16-byte blocks (the archiver pads packed data to a multiple of 16).
/// </remarks>
public sealed class Rar20Cipher {

  /// <summary>Cipher block size.</summary>
  public const int BlockSize = 16;

  private const int Rounds = 32;

  /// <summary>Longest password prefix that takes part in the key schedule.</summary>
  public const int MaxPasswordLength = 127;

  private static readonly byte[] InitialSubstitution = [
    215, 19, 149, 35, 73, 197, 192, 205, 249, 28, 16, 119, 48, 221, 2, 42,
    232, 1, 177, 233, 14, 88, 219, 25, 223, 195, 244, 90, 87, 239, 153, 137,
    255, 199, 147, 70, 92, 66, 246, 13, 216, 40, 62, 29, 217, 230, 86, 6,
    71, 24, 171, 196, 101, 113, 218, 123, 93, 91, 163, 178, 202, 67, 44, 235,
    107, 250, 75, 234, 49, 167, 125, 211, 83, 114, 157, 144, 32, 193, 143, 36,
    158, 124, 247, 187, 89, 214, 141, 47, 121, 228, 61, 130, 213, 194, 174, 251,
    97, 110, 54, 229, 115, 57, 152, 94, 105, 243, 212, 55, 209, 245, 63, 11,
    164, 200, 31, 156, 81, 176, 227, 21, 76, 99, 139, 188, 127, 17, 248, 51,
    207, 120, 189, 210, 8, 226, 41, 72, 183, 203, 135, 165, 166, 60, 98, 7,
    122, 38, 155, 170, 69, 172, 252, 238, 39, 134, 59, 128, 236, 27, 240, 80,
    131, 3, 85, 206, 145, 79, 154, 142, 159, 220, 201, 133, 74, 64, 20, 129,
    224, 185, 138, 103, 173, 182, 43, 34, 254, 82, 198, 151, 231, 180, 58, 10,
    118, 26, 102, 12, 50, 132, 22, 191, 136, 111, 162, 179, 45, 4, 148, 108,
    161, 56, 78, 126, 242, 222, 15, 175, 146, 23, 33, 241, 181, 190, 77, 225,
    0, 46, 169, 186, 68, 95, 237, 65, 53, 208, 253, 168, 9, 18, 100, 52,
    116, 184, 160, 96, 109, 37, 30, 106, 140, 104, 150, 5, 204, 117, 112, 84,
  ];

  /// <summary>The reflected CRC-32 (polynomial 0xEDB88320) table the key schedule indexes.</summary>
  private static readonly uint[] CrcTable = BuildCrcTable();

  private readonly byte[] _substitution = new byte[256];
  private readonly uint[] _keys = new uint[4];

  /// <summary>Keys the cipher with <paramref name="password"/> (the archiver's OEM-code-page bytes).</summary>
  public Rar20Cipher(ReadOnlySpan<byte> password) {
    this._keys[0] = 0xD3A3B879;
    this._keys[1] = 0x3F6D12F7;
    this._keys[2] = 0x7515A235;
    this._keys[3] = 0xA4E7F123;

    Span<byte> psw = stackalloc byte[MaxPasswordLength + 1];
    psw.Clear();
    var size = Math.Min(password.Length, MaxPasswordLength);
    password[..size].CopyTo(psw);
    InitialSubstitution.CopyTo(this._substitution, 0);

    for (var j = 0; j < 256; ++j)
      for (var i = 0; i < size; i += 2) {
        var n1 = (uint)(byte)CrcTable[(psw[i] - j) & 0xFF];
        var n2 = (uint)(byte)CrcTable[(psw[i + 1] + j) & 0xFF];
        for (var k = 1u; (n1 & 0xFF) != n2; ++n1, ++k)
          (this._substitution[n1 & 0xFF], this._substitution[(n1 + (uint)i + k) & 0xFF]) =
            (this._substitution[(n1 + (uint)i + k) & 0xFF], this._substitution[n1 & 0xFF]);
      }

    for (var i = 0; i < size; i += BlockSize)
      this.EncryptBlock(psw.Slice(i, BlockSize));
  }

  /// <summary>Decrypts whole blocks in place; a trailing partial block is left as it is.</summary>
  public void Decrypt(Span<byte> data) {
    for (var i = 0; i + BlockSize <= data.Length; i += BlockSize)
      this.DecryptBlock(data.Slice(i, BlockSize));
  }

  /// <summary>Encrypts whole blocks in place; a trailing partial block is left as it is.</summary>
  public void Encrypt(Span<byte> data) {
    for (var i = 0; i + BlockSize <= data.Length; i += BlockSize)
      this.EncryptBlock(data.Slice(i, BlockSize));
  }

  private void EncryptBlock(Span<byte> block) => this.CryptBlock(block, encrypt: true);

  private void DecryptBlock(Span<byte> block) => this.CryptBlock(block, encrypt: false);

  private uint Substitute(uint t)
    => this._substitution[t & 0xFF]
       | ((uint)this._substitution[(t >> 8) & 0xFF] << 8)
       | ((uint)this._substitution[(t >> 16) & 0xFF] << 16)
       | ((uint)this._substitution[t >> 24] << 24);

  private void CryptBlock(Span<byte> block, bool encrypt) {
    Span<byte> cipherText = stackalloc byte[BlockSize];
    if (!encrypt)
      block.CopyTo(cipherText);
    var a = BinaryPrimitives.ReadUInt32LittleEndian(block) ^ this._keys[0];
    var b = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]) ^ this._keys[1];
    var c = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]) ^ this._keys[2];
    var d = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]) ^ this._keys[3];
    for (var i = 0; i < Rounds; ++i) {
      var key = this._keys[(encrypt ? i : Rounds - 1 - i) & 3];
      var ta = a ^ this.Substitute((c + BitOperations.RotateLeft(d, 11)) ^ key);
      var tb = b ^ this.Substitute((d ^ BitOperations.RotateLeft(c, 17)) + key);
      a = c; c = ta;
      b = d; d = tb;
    }
    BinaryPrimitives.WriteUInt32LittleEndian(block, c ^ this._keys[0]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[4..], d ^ this._keys[1]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[8..], a ^ this._keys[2]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[12..], b ^ this._keys[3]);
    ReadOnlySpan<byte> absorbed = encrypt ? block : cipherText;
    for (var i = 0; i < BlockSize; i += 4)
      for (var j = 0; j < 4; ++j)
        this._keys[j] ^= CrcTable[absorbed[i + j]];
  }

  private static uint[] BuildCrcTable() {
    var table = new uint[256];
    for (var n = 0u; n < 256; ++n) {
      var c = n;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
      table[n] = c;
    }
    return table;
  }
}
