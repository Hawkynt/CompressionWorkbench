using Compression.Core.Checksums;

namespace Compression.Core.Crypto;

/// <summary>
/// The stream ciphers of RAR 1.3/1.4 and RAR 1.5 (archives with unpack version 13 and 15):
/// byte-wise keystreams derived from the password. Both are weak; they are read only.
/// </summary>
/// <remarks>
/// Converted from XADMaster's <c>XADRAR13CryptHandle.m</c> and <c>XADRAR15CryptHandle.m</c>
/// (MacPaw, LGPL-2.1-or-later, revision 7cb9ee0, see <c>THIRD-PARTY-NOTICE.XADMaster.txt</c>).
/// The password is the OEM bytes RAR of that era read from the keyboard.
/// </remarks>
public sealed class Rar15Cipher {
  private static readonly uint[] CrcTable = BuildCrcTable();

  private readonly bool _v13;
  private ushort _k0, _k1, _k2, _k3;
  private byte _b1, _b2, _b3;

  /// <summary>Keys the cipher of <paramref name="unpackVersion"/> (13 or 15) with <paramref name="password"/>.</summary>
  public Rar15Cipher(ReadOnlySpan<byte> password, int unpackVersion) {
    this._v13 = unpackVersion < 15;
    if (this._v13) {
      foreach (var c in password) {
        this._b1 += c;
        this._b2 ^= c;
        this._b3 += c;
        this._b3 = (byte)((this._b3 << 1) | (this._b3 >> 7));
      }
      return;
    }
    var crc = ~Crc32.Compute(password); // register after the bytes, without the final inversion
    this._k0 = (ushort)crc;
    this._k1 = (ushort)(crc >> 16);
    foreach (var c in password) {
      this._k2 ^= (ushort)(c ^ CrcTable[c]);
      this._k3 += (ushort)(c + (CrcTable[c] >> 16));
    }
  }

  /// <summary>Decrypts <paramref name="data"/> in place, continuing the keystream.</summary>
  public void Decrypt(Span<byte> data) {
    for (var i = 0; i < data.Length; ++i) {
      if (this._v13) {
        this._b2 += this._b3;
        this._b1 += this._b2;
        data[i] = (byte)(data[i] - this._b1);
        continue;
      }
      this._k0 += 0x1234;
      var t = CrcTable[(this._k0 >> 1) & 0xFF];
      this._k1 ^= (ushort)t;
      this._k2 -= (ushort)(t >> 16);
      this._k0 ^= this._k2;
      this._k3 = (ushort)(Ror16(this._k3, 1) ^ this._k1);
      this._k3 = Ror16(this._k3, 1);
      this._k0 ^= this._k3;
      data[i] ^= (byte)(this._k0 >> 8);
    }
  }

  private static ushort Ror16(ushort v, int n) => (ushort)((v >> n) | (v << (16 - n)));

  private static uint[] BuildCrcTable() {
    var t = new uint[256];
    for (var n = 0u; n < 256; ++n) {
      var c = n;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      t[n] = c;
    }
    return t;
  }
}
