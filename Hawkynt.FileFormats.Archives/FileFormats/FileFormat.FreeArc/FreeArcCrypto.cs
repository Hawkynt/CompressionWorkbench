using System.Security.Cryptography;
using System.Text;
using Compression.Core.Crypto;

namespace FileFormat.FreeArc;

/// <summary>
/// FreeArc's encryption stages, <c>cipher[-bits][/mode]:nITER:rROUNDS:iIV:sSALT:cCHECK[:f]</c>,
/// written from docs/FREEARC-ON-DISK.md (section "Encryption").
/// </summary>
/// <remarks>
/// The key and a check code come from PBKDF2-HMAC-SHA-512 over the password and the stored salt;
/// the check code tells a wrong password apart before any data is touched. Without <c>:f</c> the
/// key and the initial vector reach the cipher through a hex decoding that reads the digits
/// <c>a</c>..<c>f</c> as 0..5, and that is reproduced. AES, Blowfish, Serpent and Twofish run in
/// counter or full-block cipher-feedback mode.
/// </remarks>
internal static class FreeArcCrypto {
  private static readonly string[] Ciphers = ["aes", "blowfish", "serpent", "twofish"];

  /// <summary>Gets whether <paramref name="stage"/> names one of FreeArc's ciphers.</summary>
  public static bool IsEncryption(string stage) {
    var name = stage.Split(':')[0].Trim().ToLowerInvariant();
    return Ciphers.Any(c => name == c || name.StartsWith(c + "-", StringComparison.Ordinal) || name.StartsWith(c + "/", StringComparison.Ordinal));
  }

  /// <summary>Gets whether <paramref name="stage"/> is an encryption stage this decoder can undo given the password.</summary>
  public static bool IsDecodable(string stage) => Stage.TryParse(stage, out _);

  /// <summary>Decrypts one encryption stage with <paramref name="password"/>.</summary>
  /// <exception cref="InvalidDataException">The password is wrong, or the stage is malformed.</exception>
  public static byte[] Decrypt(string stageText, byte[] input, string password) {
    if (!Stage.TryParse(stageText, out var stage))
      throw new NotSupportedException($"FreeArc: encryption \"{stageText}\" is not supported.");
    var key = stage.Key ?? DeriveKey(stage, password);
    using var aes = stage.Cipher == "aes" ? Aes.Create() : null;
    var cipher = CreateCipher(stage.Cipher, key, aes);
    var output = input.ToArray();
    if (stage.Feedback)
      DecryptCfb(cipher, stage.Iv, output);
    else
      ApplyCtr(cipher, stage.Iv, output, sevenZipCounter: stage.Cipher == "aes");
    return output;
  }

  private static byte[] DeriveKey(Stage stage, string password) {
    if (stage.Salt is null)
      throw new InvalidDataException("FreeArc: the encryption stage records no salt.");
    var keyLength = stage.KeyBytes;
    // FreeArc 0.67 hands the password over one byte per character; ":f" marks UTF-8.
    IEnumerable<byte[]> candidates = stage.Fixed
      ? [Encoding.UTF8.GetBytes(password)]
      : [password.Select(ch => (byte)ch).ToArray(), Encoding.UTF8.GetBytes(password)];
    foreach (var bytes in candidates) {
      var derived = Rfc2898DeriveBytes.Pbkdf2(bytes, stage.Salt, stage.Iterations, HashAlgorithmName.SHA512, keyLength + stage.Check.Length);
      if (!derived.AsSpan(keyLength).SequenceEqual(stage.Check))
        continue;
      var key = derived[..keyLength];
      return stage.Fixed ? key : key.Select(b => (byte)((LooseNibble(b >> 4) << 4) | LooseNibble(b & 15))).ToArray();
    }
    throw new InvalidDataException("FreeArc: wrong password (the check code does not match).");
  }

  /// <summary>A nibble after the hex round trip that reads <c>a</c>..<c>f</c> as 0..5.</summary>
  private static int LooseNibble(int digit) => digit < 10 ? digit : digit - 10;

  private sealed record BlockCipher(int BlockSize, Action<Span<byte>> Encrypt);

  private static BlockCipher CreateCipher(string name, byte[] key, Aes? aes) {
    switch (name) {
      case "aes": {
        aes!.Key = key;
        return new(16, block => aes.EncryptEcb(block, block, PaddingMode.None));
      }
      case "blowfish": {
        var blowfish = new Blowfish(key);
        return new(8, blowfish.Encrypt);
      }
      case "serpent": {
        var serpent = new Serpent(key);
        return new(16, serpent.Encrypt);
      }
      default: {
        var twofish = new Twofish(key);
        return new(16, twofish.Encrypt);
      }
    }
  }

  /// <summary>
  /// Counter mode with a little-endian counter starting at the initial vector. AES counts in the
  /// low 64 bits only (the counter is decremented across all 16 bytes first, then incremented
  /// before each block); the other ciphers carry across the whole block.
  /// </summary>
  private static void ApplyCtr(BlockCipher cipher, byte[] iv, Span<byte> data, bool sevenZipCounter) {
    var n = cipher.BlockSize;
    var counter = iv.ToArray();
    Span<byte> stream = stackalloc byte[16];
    if (sevenZipCounter)
      for (var i = 0; i < n && counter[i]-- == 0; ++i) {
      }
    for (var at = 0; at < data.Length; at += n) {
      if (sevenZipCounter)
        for (var i = 0; i < 8 && ++counter[i] == 0; ++i) {
        }
      counter.CopyTo(stream);
      cipher.Encrypt(stream[..n]);
      if (!sevenZipCounter)
        for (var i = 0; i < n && ++counter[i] == 0; ++i) {
        }
      var chunk = data.Slice(at, Math.Min(n, data.Length - at));
      for (var i = 0; i < chunk.Length; ++i)
        chunk[i] ^= stream[i];
    }
  }

  /// <summary>Cipher feedback over whole blocks: each keystream block encrypts the previous ciphertext block.</summary>
  private static void DecryptCfb(BlockCipher cipher, byte[] iv, Span<byte> data) {
    var n = cipher.BlockSize;
    Span<byte> register = stackalloc byte[16];
    iv.CopyTo(register);
    for (var at = 0; at < data.Length; at += n) {
      cipher.Encrypt(register[..n]);
      var chunk = data.Slice(at, Math.Min(n, data.Length - at));
      for (var i = 0; i < chunk.Length; ++i) {
        var c = chunk[i];
        chunk[i] ^= register[i];
        register[i] = c;
      }
    }
  }

  /// <summary>A parsed encryption stage.</summary>
  private sealed record Stage(string Cipher, int KeyBytes, bool Feedback, int Iterations, byte[] Iv, byte[]? Salt, byte[] Check, byte[]? Key, bool Fixed) {
    public static bool TryParse(string text, out Stage stage) {
      stage = null!;
      var tokens = text.Split(':');
      var head = tokens[0].Trim().ToLowerInvariant();
      var feedback = false;
      var slash = head.IndexOf('/');
      if (slash >= 0) {
        switch (head[(slash + 1)..]) {
          case "ctr": break;
          case "cfb": feedback = true; break;
          default: return false;
        }
        head = head[..slash];
      }
      var dash = head.IndexOf('-');
      var cipher = dash < 0 ? head : head[..dash];
      if (!Ciphers.Contains(cipher))
        return false;
      var blockSize = cipher == "blowfish" ? 8 : 16;
      var keyBytes = cipher == "blowfish" ? 56 : 32;
      if (dash >= 0) {
        if (!int.TryParse(head.AsSpan(dash + 1), out var bits) || bits % 8 != 0)
          return false;
        keyBytes = bits / 8;
      }
      // Blowfish takes 8 to 56 key bytes, the 128-bit-block ciphers 16, 24 or 32.
      if (cipher == "blowfish" ? keyBytes is < 8 or > 56 : keyBytes is not (16 or 24 or 32))
        return false;

      int iterations = 1000, rounds = 0;
      string? iv = null, salt = null, check = null, key = null;
      var isFixed = false;
      foreach (var raw in tokens.Skip(1)) {
        var token = raw.Trim();
        if (token == "f") {
          isFixed = true;
          continue;
        }
        if (token.Length == 0)
          return false;
        var value = token[1..];
        switch (token[0]) {
          case 'n' when int.TryParse(value, out iterations) && iterations > 0: break;
          case 'r' when int.TryParse(value, out rounds): break;
          case 'i': iv = value; break;
          case 's': salt = value; break;
          case 'c': check = value; break;
          case 'k': key = value; break;
          default: return false;
        }
      }
      if (rounds != 0 || iv is null || (salt is null && key is null))
        return false;
      if (!TryHex(iv, isFixed, out var ivBytes) || ivBytes.Length != blockSize)
        return false;
      byte[]? saltBytes = null, keyBytesValue = null;
      if (salt is not null && !TryHex(salt, true, out saltBytes))
        return false;
      if (key is not null && (!TryHex(key, isFixed, out keyBytesValue) || keyBytesValue.Length != keyBytes))
        return false;
      if (!TryHex(check ?? "", true, out var checkBytes))
        return false;
      stage = new(cipher, keyBytes, feedback, iterations, ivBytes, saltBytes, checkBytes, keyBytesValue, isFixed);
      return true;
    }

    /// <summary>Hex digits to bytes; when <paramref name="strict"/> is false, <c>a</c>..<c>f</c> count as 0..5.</summary>
    private static bool TryHex(string hex, bool strict, out byte[] bytes) {
      bytes = [];
      if (hex.Length % 2 != 0 || !hex.All(char.IsAsciiHexDigit))
        return false;
      bytes = new byte[hex.Length / 2];
      for (var i = 0; i < bytes.Length; ++i)
        bytes[i] = (byte)(Digit(hex[2 * i], strict) * 16 + Digit(hex[2 * i + 1], strict));
      return true;
    }

    private static int Digit(char c, bool strict) {
      if (char.IsAsciiDigit(c))
        return c - '0';
      var v = char.ToLowerInvariant(c) - 'a';
      return strict ? v + 10 : v;
    }
  }
}
