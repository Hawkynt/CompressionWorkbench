// B1 serialization primitives, converted from b1-pack (https://github.com/b1-pack/b1-pack,
// Apache-2.0, Copyright 2011 b1.org; changed: C# conversion). See THIRD-PARTY-NOTICE.b1-pack.txt.

using System.Security.Cryptography;
using System.Text;

namespace FileFormat.B1;

/// <summary>
/// The B1 "int" (b1-pack Numbers): sign in the lowest bit, 6 value bits in the first byte and 7 in
/// each continuation byte, high bit = more follows; null is a negative zero (a lone 0x01).
/// </summary>
internal static class B1Int {
  public static long? Read(Stream s) {
    var b = ReadByte(s);
    var sign = b & 1;
    var value = (long)((b >> 1) & 0x3F);
    for (var shift = 6; (b & 0x80) != 0; shift += 7) {
      b = ReadByte(s);
      long bits = b & 0x7F;
      if (shift >= 62 && bits != 0 && (shift > 62 || (bits == 2 ? value != 0 : bits != 1)))
        throw new InvalidDataException("B1 integer is too long.");
      value |= bits << shift;
    }

    return sign == 0 ? value : value == 0 ? null : -value;
  }

  public static long ReadRequired(Stream s) => Read(s) ?? throw new InvalidDataException("Unexpected null B1 integer.");

  private static int ReadByte(Stream s) {
    var b = s.ReadByte();
    return b < 0 ? throw new InvalidDataException("Truncated B1 data.") : b;
  }

  public static void Write(Stream s, long? value) {
    if (value == null) {
      s.WriteByte(1);
      return;
    }

    var v = value.Value;
    var sign = 0;
    if (v < 0) {
      sign = 1;
      v = -v;
    }

    var b = (int)((v & 0x3F) << 1) | sign;
    var rest = (ulong)v >> 6;
    while (rest != 0) {
      s.WriteByte((byte)(b | 0x80));
      b = (int)(rest & 0x7F);
      rest >>= 7;
    }

    s.WriteByte((byte)b);
  }

  /// <summary>Reads a "binary": parts of int length + bytes, ending with a zero length.</summary>
  public static byte[]? ReadBinary(Stream s) {
    using var ms = new MemoryStream();
    var first = true;
    while (true) {
      var length = Read(s);
      if (length == null)
        return first ? null : throw new InvalidDataException("Null inside a B1 binary.");
      if (length == 0)
        return ms.ToArray();
      if (length < 0 || length > int.MaxValue)
        throw new InvalidDataException("B1 binary part length is out of range.");
      var buffer = new byte[length.Value];
      s.ReadExactly(buffer);
      ms.Write(buffer);
      first = false;
    }
  }

  public static void WriteBinary(Stream s, ReadOnlySpan<byte> data) {
    if (data.Length > 0) {
      Write(s, data.Length);
      s.Write(data);
    }

    Write(s, 0);
  }

  public static string? ReadText(Stream s) {
    var length = Read(s);
    if (length == null)
      return null;
    if (length < 0 || length > int.MaxValue)
      throw new InvalidDataException("B1 text length is out of range.");
    var bytes = new byte[length.Value];
    s.ReadExactly(bytes);
    return Encoding.UTF8.GetString(bytes);
  }

  public static void WriteText(Stream s, string value) {
    var bytes = Encoding.UTF8.GetBytes(value);
    Write(s, bytes.Length);
    s.Write(bytes);
  }

  public static uint Adler32(ReadOnlySpan<byte> data) {
    uint a = 1, b = 0;
    foreach (var x in data) {
      a = (a + x) % 65521;
      b = (b + a) % 65521;
    }

    return (b << 16) | a;
  }
}

/// <summary>
/// B1 encryption (b1-pack PackCipher/VolumeCipher): PBKDF2-HMAC-SHA256 archive key from the password
/// and the base64 archive id; per-volume, head (0x01), tail (0x02) and per-block (decimal offset)
/// keys derived by HMAC-SHA256; AES-256-GCM with a zero 96-bit nonce and the 16-byte tag appended.
/// </summary>
internal sealed class B1VolumeCipher {
  private const int TagSize = 16;
  private readonly byte[] _volumeKey;

  public int IterationCount { get; }

  public B1VolumeCipher(string password, byte[] salt, int iterationCount, long volumeNumber) {
    this.IterationCount = iterationCount;
    var archiveKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterationCount, HashAlgorithmName.SHA256, 32);
    this._volumeKey = HMACSHA256.HashData(archiveKey, Encoding.UTF8.GetBytes(volumeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
  }

  private byte[] Key(ReadOnlySpan<byte> salt) => HMACSHA256.HashData(this._volumeKey, salt);

  public byte[] Head(bool encrypt, byte[] data) => Cipher(encrypt, [0x01], data);

  public byte[] Tail(bool encrypt, byte[] data) => Cipher(encrypt, [0x02], data);

  public byte[] Block(bool encrypt, long offset, byte[] data) =>
    Cipher(encrypt, Encoding.UTF8.GetBytes(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)), data);

  private byte[] Cipher(bool encrypt, ReadOnlySpan<byte> salt, byte[] data) {
    using var gcm = new AesGcm(this.Key(salt), TagSize);
    var nonce = new byte[12];
    if (encrypt) {
      var output = new byte[data.Length + TagSize];
      gcm.Encrypt(nonce, data, output.AsSpan(0, data.Length), output.AsSpan(data.Length));
      return output;
    }

    if (data.Length < TagSize)
      throw new InvalidDataException("B1 encrypted item is shorter than its tag.");
    var plain = new byte[data.Length - TagSize];
    try {
      gcm.Decrypt(nonce, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain);
    } catch (AuthenticationTagMismatchException e) {
      throw new InvalidDataException("Wrong B1 password, or the archive is corrupt.", e);
    }

    return plain;
  }
}
