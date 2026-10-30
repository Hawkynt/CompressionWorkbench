using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Compression.Core.Crypto;

namespace FileFormat.Pdf;

/// <summary>
/// PDF Standard security handler: derives the file key from a password and decrypts the strings
/// and streams of an encrypted PDF.
/// </summary>
/// <remarks>
/// Implemented from ISO 32000-1:2008 §7.6 (algorithms 1, 2, 4, 5, 7: revisions 2-4, RC4 40-128
/// bit and AES-128 via the AESV2 crypt filter) and ISO 32000-2:2020 §7.6.4.3-4 (revision 6, AES-256,
/// algorithm 2.B), plus Adobe's Extension Level 3 revision 5 (plain SHA-256). The password is tried
/// as user password, then as owner password. Public-key handlers and custom crypt filters are
/// refused.
/// </remarks>
public sealed partial class PdfStandardSecurity {

  private static readonly byte[] PasswordPadding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

  private readonly byte[] _fileKey;
  private readonly bool _aes;
  private readonly bool _aes256;

  /// <summary>The object number of the /Encrypt dictionary (its strings are never encrypted).</summary>
  public int EncryptObjectNumber { get; }

  /// <summary>Revision of the handler (2-6).</summary>
  public int Revision { get; }

  /// <summary>"RC4" or "AES".</summary>
  public string Cipher => this._aes ? "AES" : "RC4";

  /// <summary>Key length in bits.</summary>
  public int KeyBits => this._fileKey.Length * 8;

  private PdfStandardSecurity(byte[] fileKey, bool aes, bool aes256, int revision, int encryptObject) {
    this._fileKey = fileKey;
    this._aes = aes;
    this._aes256 = aes256;
    this.Revision = revision;
    this.EncryptObjectNumber = encryptObject;
  }

  /// <summary>
  /// Opens the security handler of <paramref name="text"/> (the PDF as Latin-1), or returns null when
  /// the file is not encrypted.
  /// </summary>
  /// <exception cref="NotSupportedException">A non-Standard handler or an unsupported crypt filter.</exception>
  /// <exception cref="UnauthorizedAccessException">The password opens neither as user nor as owner.</exception>
  public static PdfStandardSecurity? Open(string text, string? password) {
    var trailer = EncryptRefPattern().Matches(text);
    if (trailer.Count == 0)
      return null;
    var encryptObject = int.Parse(trailer[^1].Groups[1].Value, CultureInfo.InvariantCulture);
    var dictMatch = new Regex($@"(?<![0-9]){encryptObject}\s+\d+\s+obj\s*(.*?)endobj", RegexOptions.Singleline).Matches(text);
    if (dictMatch.Count == 0)
      throw new InvalidDataException($"PDF /Encrypt object {encryptObject} not found.");
    var dict = dictMatch[^1].Groups[1].Value;

    var filter = NameValue(dict, "Filter");
    if (filter != "Standard")
      throw new NotSupportedException($"PDF security handler /{filter} is not supported (only /Standard).");
    var v = IntValue(dict, "V") ?? 0;
    var r = IntValue(dict, "R") ?? throw new InvalidDataException("PDF /Encrypt lacks /R.");
    var p = unchecked((int)(LongValue(dict, "P") ?? 0)); // some writers store /P unsigned
    var o = StringValue(dict, "O") ?? throw new InvalidDataException("PDF /Encrypt lacks /O.");
    var u = StringValue(dict, "U") ?? throw new InvalidDataException("PDF /Encrypt lacks /U.");
    var encryptMetadata = !Regex.IsMatch(dict, @"/EncryptMetadata\s+false");
    var pw = Encoding.Latin1.GetBytes(password ?? "");

    if (r >= 5) {
      if (v != 5 || !dict.Contains("/AESV3"))
        throw new NotSupportedException("PDF revision 5/6 encryption requires the AESV3 crypt filter.");
      var oe = StringValue(dict, "OE") ?? throw new InvalidDataException("PDF /Encrypt lacks /OE.");
      var ue = StringValue(dict, "UE") ?? throw new InvalidDataException("PDF /Encrypt lacks /UE.");
      var pw6 = Encoding.UTF8.GetBytes(password ?? "");
      if (pw6.Length > 127) pw6 = pw6[..127];
      var key = Revision6Key(pw6, u, ue, o, oe, r)
        ?? throw new UnauthorizedAccessException("The password does not open this PDF.");
      return new PdfStandardSecurity(key, aes: true, aes256: true, r, encryptObject);
    }

    var aes = false;
    if (v == 4) {
      var stmF = NameValue(dict, "StmF") ?? "Identity";
      var cfm = Regex.Match(dict, $@"/{Regex.Escape(stmF)}\s*<<[^>]*?/CFM\s*/(\w+)").Groups[1].Value;
      aes = cfm switch {
        "AESV2" => true,
        "V2" => false,
        _ => throw new NotSupportedException($"PDF crypt filter method /{cfm} is not supported."),
      };
    } else if (v is not (1 or 2))
      throw new NotSupportedException($"PDF encryption version /V {v} is not supported.");
    // V4 crypt filters are always 128-bit (their own /Length is in bytes, the dictionary's in bits).
    var keyBytes = v switch { 1 => 5, 4 => 16, _ => (IntValue(dict, "Length") ?? 40) / 8 };
    if (keyBytes is < 5 or > 16)
      throw new InvalidDataException($"PDF key length {keyBytes * 8} is out of range.");
    var id0 = FirstIdString(text) ?? [];

    var fileKey = UserKey(pw, o, p, id0, r, keyBytes, encryptMetadata);
    if (!CheckUser(fileKey, u, id0, r)) {
      fileKey = UserKey(OwnerToUser(pw, o, r, keyBytes), o, p, id0, r, keyBytes, encryptMetadata);
      if (!CheckUser(fileKey, u, id0, r))
        throw new UnauthorizedAccessException("The password does not open this PDF.");
    }
    return new PdfStandardSecurity(fileKey, aes, aes256: false, r, encryptObject);
  }

  /// <summary>Decrypts a string or stream belonging to object <paramref name="objectNumber"/>.</summary>
  public byte[] Decrypt(ReadOnlySpan<byte> data, int objectNumber, int generation) {
    if (objectNumber == this.EncryptObjectNumber)
      return data.ToArray();
    var key = this._aes256 ? this._fileKey : this.ObjectKey(objectNumber, generation);
    if (!this._aes)
      return Rc4.Apply(key, data);
    if (data.Length < 16 || data.Length % 16 != 0)
      return data.Length == 0 ? [] : throw new InvalidDataException($"PDF object {objectNumber}: AES data is not a whole number of blocks.");
    using var cipher = Aes.Create();
    cipher.Key = key;
    return cipher.DecryptCbc(data[16..], data[..16], PaddingMode.PKCS7);
  }

  private byte[] ObjectKey(int objectNumber, int generation) {
    var input = new byte[this._fileKey.Length + 5 + (this._aes ? 4 : 0)];
    this._fileKey.CopyTo(input, 0);
    var n = this._fileKey.Length;
    input[n] = (byte)objectNumber;
    input[n + 1] = (byte)(objectNumber >> 8);
    input[n + 2] = (byte)(objectNumber >> 16);
    input[n + 3] = (byte)generation;
    input[n + 4] = (byte)(generation >> 8);
    if (this._aes)
      "sAlT"u8.CopyTo(input.AsSpan(n + 5));
    return MD5.HashData(input)[..Math.Min(n + 5, 16)];
  }

  /// <summary>Algorithm 2: the file key from a user password.</summary>
  private static byte[] UserKey(byte[] password, byte[] o, int p, byte[] id0, int r, int keyBytes, bool encryptMetadata) {
    using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    md5.AppendData(Pad(password));
    md5.AppendData(o.AsSpan(0, Math.Min(32, o.Length)));
    Span<byte> pb = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(pb, p);
    md5.AppendData(pb);
    md5.AppendData(id0);
    if (r >= 4 && !encryptMetadata)
      md5.AppendData([0xFF, 0xFF, 0xFF, 0xFF]);
    var hash = md5.GetHashAndReset();
    if (r >= 3)
      for (var i = 0; i < 50; ++i)
        hash = MD5.HashData(hash.AsSpan(0, keyBytes));
    return hash[..keyBytes];
  }

  /// <summary>Algorithms 4 and 5: whether <paramref name="key"/> reproduces /U.</summary>
  private static bool CheckUser(byte[] key, byte[] u, byte[] id0, int r) {
    if (r == 2)
      return Rc4.Apply(key, PasswordPadding).AsSpan().SequenceEqual(u.AsSpan(0, Math.Min(32, u.Length)));
    var x = MD5.HashData([.. PasswordPadding, .. id0]);
    x = Rc4.Apply(key, x);
    for (var i = 1; i <= 19; ++i)
      x = Rc4.Apply(XorKey(key, i), x);
    return u.Length >= 16 && x.AsSpan().SequenceEqual(u.AsSpan(0, 16));
  }

  /// <summary>Algorithm 7: recovers the user password from /O and an owner password.</summary>
  private static byte[] OwnerToUser(byte[] ownerPassword, byte[] o, int r, int keyBytes) {
    var hash = MD5.HashData(Pad(ownerPassword));
    if (r >= 3)
      for (var i = 0; i < 50; ++i)
        hash = MD5.HashData(hash);
    var key = hash[..keyBytes];
    var user = o.AsSpan(0, Math.Min(32, o.Length)).ToArray();
    if (r == 2)
      return Rc4.Apply(key, user);
    for (var i = 19; i >= 0; --i)
      user = Rc4.Apply(XorKey(key, i), user);
    return user;
  }

  private static byte[] XorKey(byte[] key, int value) {
    var k = (byte[])key.Clone();
    for (var j = 0; j < k.Length; ++j)
      k[j] ^= (byte)value;
    return k;
  }

  private static byte[] Pad(byte[] password) {
    var padded = new byte[32];
    var n = Math.Min(32, password.Length);
    password.AsSpan(0, n).CopyTo(padded);
    PasswordPadding.AsSpan(0, 32 - n).CopyTo(padded.AsSpan(n));
    return padded;
  }

  /// <summary>Revisions 5/6: the AES-256 file key, from the user or owner password; null when neither opens.</summary>
  private static byte[]? Revision6Key(byte[] pw, byte[] u, byte[] ue, byte[] o, byte[] oe, int r) {
    if (u.Length < 48 || o.Length < 48 || ue.Length < 32 || oe.Length < 32)
      throw new InvalidDataException("PDF /U, /O, /UE or /OE is too short for revision 5/6.");
    var userData = u.AsSpan(0, 48).ToArray();
    if (Hash(pw, u.AsSpan(32, 8), [], r).AsSpan().SequenceEqual(u.AsSpan(0, 32)))
      return UnwrapKey(Hash(pw, u.AsSpan(40, 8), [], r), ue);
    if (Hash(pw, o.AsSpan(32, 8), userData, r).AsSpan().SequenceEqual(o.AsSpan(0, 32)))
      return UnwrapKey(Hash(pw, o.AsSpan(40, 8), userData, r), oe);
    return null;
  }

  private static byte[] UnwrapKey(byte[] intermediate, byte[] wrapped) {
    using var cipher = Aes.Create();
    cipher.Key = intermediate;
    return cipher.DecryptCbc(wrapped.AsSpan(0, 32), new byte[16], PaddingMode.None);
  }

  /// <summary>Algorithm 2.B (revision 6) or plain SHA-256 (revision 5).</summary>
  private static byte[] Hash(byte[] password, ReadOnlySpan<byte> salt, byte[] userData, int r) {
    var k = SHA256.HashData([.. password, .. salt, .. userData]);
    if (r == 5)
      return k;
    using var cipher = Aes.Create();
    for (var round = 0; ; ++round) {
      var k1Unit = new byte[password.Length + k.Length + userData.Length];
      password.CopyTo(k1Unit, 0);
      k.CopyTo(k1Unit, password.Length);
      userData.CopyTo(k1Unit, password.Length + k.Length);
      var k1 = new byte[k1Unit.Length * 64];
      for (var i = 0; i < 64; ++i)
        k1Unit.CopyTo(k1, i * k1Unit.Length);
      cipher.Key = k[..16];
      var e = cipher.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
      var mod = 0;
      for (var i = 0; i < 16; ++i)
        mod += e[i];
      k = (mod % 3) switch {
        0 => SHA256.HashData(e),
        1 => SHA384.HashData(e),
        _ => SHA512.HashData(e),
      };
      if (round >= 63 && e[^1] <= round - 31)
        break;
    }
    return k[..32];
  }

  private static string? NameValue(string dict, string key) {
    var m = Regex.Match(dict, $@"/{key}\s*/(\w+)");
    return m.Success ? m.Groups[1].Value : null;
  }

  private static int? IntValue(string dict, string key) => LongValue(dict, key) is { } l ? checked((int)l) : null;

  private static long? LongValue(string dict, string key) {
    var m = Regex.Match(dict, $@"/{key}\s+(-?\d+)\b(?!\s+\d+\s+R)");
    return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
  }

  /// <summary>The value of a string entry, literal or hexadecimal, as raw bytes.</summary>
  private static byte[]? StringValue(string dict, string key) {
    var m = Regex.Match(dict, $@"/{key}\s*([(<])");
    if (!m.Success)
      return null;
    var start = m.Groups[1].Index;
    return dict[start] == '<' ? ParseHexString(dict, start, out _) : ParseLiteralString(dict, start, out _);
  }

  private static byte[]? FirstIdString(string text) {
    var ids = IdPattern().Matches(text);
    if (ids.Count == 0)
      return null;
    var m = ids[^1];
    var start = m.Groups[1].Index;
    return text[start] == '<' ? ParseHexString(text, start, out _) : ParseLiteralString(text, start, out _);
  }

  /// <summary>Parses the hexadecimal string that opens at <paramref name="start"/> (the '&lt;').</summary>
  public static byte[] ParseHexString(string s, int start, out int end) {
    var close = s.IndexOf('>', start);
    if (close < 0) throw new InvalidDataException("Unterminated PDF hex string.");
    end = close + 1;
    var hex = new StringBuilder();
    foreach (var c in s.AsSpan(start + 1, close - start - 1))
      if (char.IsAsciiHexDigit(c)) hex.Append(c);
    if (hex.Length % 2 != 0) hex.Append('0');
    return Convert.FromHexString(hex.ToString());
  }

  /// <summary>Parses the literal string that opens at <paramref name="start"/> (the '('), resolving escapes.</summary>
  public static byte[] ParseLiteralString(string s, int start, out int end) {
    var bytes = new List<byte>();
    var depth = 0;
    for (var i = start + 1; i < s.Length; ++i) {
      var c = s[i];
      if (c == '\\' && i + 1 < s.Length) {
        var n = s[++i];
        switch (n) {
          case 'n': bytes.Add(0x0A); break;
          case 'r': bytes.Add(0x0D); break;
          case 't': bytes.Add(0x09); break;
          case 'b': bytes.Add(0x08); break;
          case 'f': bytes.Add(0x0C); break;
          case '\r':
            if (i + 1 < s.Length && s[i + 1] == '\n') ++i;
            break;
          case '\n': break;
          case >= '0' and <= '7': {
            var value = n - '0';
            for (var k = 0; k < 2 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7'; ++k)
              value = value * 8 + (s[++i] - '0');
            bytes.Add((byte)value);
            break;
          }
          default: bytes.Add((byte)n); break;
        }
        continue;
      }
      if (c == '(') ++depth;
      else if (c == ')') {
        if (depth == 0) { end = i + 1; return [.. bytes]; }
        --depth;
      }
      bytes.Add((byte)c);
    }
    throw new InvalidDataException("Unterminated PDF literal string.");
  }

  [GeneratedRegex(@"/Encrypt\s+(\d+)\s+\d+\s+R")]
  private static partial Regex EncryptRefPattern();

  [GeneratedRegex(@"/ID\s*\[\s*([(<])")]
  private static partial Regex IdPattern();
}
