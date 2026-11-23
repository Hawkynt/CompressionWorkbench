// Compact Pro encryption, converted from MacExtract (https://github.com/uzTools/MacExtract, 0BSD,
// revision 4b507c21c03377e2224c5c0f4a7eb9be69bbb30e: src/unc_crypt.c, src/fmt_cpt.c).
// See THIRD-PARTY-NOTICE.MacExtract.txt.

namespace FileFormat.CompactPro;

/// <summary>
/// Compact Pro's entry cipher: a 64-bit reflected CRC register (polynomial 0xEDB88320 in both
/// halves, seeded 0x0123456789ABCDEF) is folded over the key and then over every plaintext byte; the
/// register's second-lowest byte, taken before each step, is the keystream byte. The key is nine
/// bytes: the password length capped at eight, then the password's bytes added into eight
/// accumulators in rotation; only the accumulators (as many as that length) enter the register.
/// One keyed register runs across an entry's resource fork and then its data fork.
/// </summary>
internal sealed class CompactProCipher {
  /// <summary>Bytes in front of an encrypted entry's stream, decrypted and discarded; counted in the first non-empty fork's packed size.</summary>
  public const int PreambleLength = 8;

  private static readonly ulong[] Table = BuildTable();

  private ulong _register;

  private static ulong[] BuildTable() {
    const ulong polynomial = 0xEDB88320EDB88320UL;
    var table = new ulong[256];
    for (var i = 0; i < 256; ++i) {
      var r = (ulong)i;
      for (var bit = 0; bit < 8; ++bit)
        r = (r & 1) != 0 ? (r >> 1) ^ polynomial : r >> 1;
      table[i] = r;
    }
    return table;
  }

  /// <summary>Keys the register with <paramref name="password"/> (Mac Roman bytes).</summary>
  public CompactProCipher(ReadOnlySpan<byte> password) {
    Span<byte> key = stackalloc byte[9];
    for (var i = 0; i < password.Length; ++i)
      key[(i & 7) + 1] += password[i];
    var length = Math.Min(password.Length, 8);
    this._register = 0x0123456789ABCDEFUL;
    for (var i = 1; i <= length; ++i)
      this.Step(key[i]);
  }

  private void Step(byte plain) => this._register = (this._register >> 8) ^ Table[(byte)(this._register ^ plain)];

  private byte Keystream => (byte)(this._register >> 8);

  /// <summary>Decrypts <paramref name="buffer"/> in place, continuing from the bytes before it.</summary>
  public void Decrypt(Span<byte> buffer) {
    for (var i = 0; i < buffer.Length; ++i) {
      buffer[i] ^= this.Keystream;
      this.Step(buffer[i]);
    }
  }

  /// <summary>Encrypts <paramref name="buffer"/> in place, continuing from the bytes before it.</summary>
  public void Encrypt(Span<byte> buffer) {
    for (var i = 0; i < buffer.Length; ++i) {
      var plain = buffer[i];
      buffer[i] ^= this.Keystream;
      this.Step(plain);
    }
  }
}
