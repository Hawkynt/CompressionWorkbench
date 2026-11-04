// EGG member encryption, converted from EggDotNet (https://github.com/akolman/EggDotNet, MIT, Copyright (c)
// 2026 Andrew Kolman); see THIRD-PARTY-NOTICE.EggDotNet.txt. LEA itself is the Korean standard block
// cipher (TTAS.KO-12.0223, ISO/IEC 29192-2).

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.Egg;

/// <summary>
/// The encryption field of an EGG file header: method 0 ZIP 2.0 ("standard"), 1/2 AES-128/256 and
/// 5/6 LEA-128/256, with the per-method header (salt + password check) and footer (MAC or CRC part).
/// </summary>
internal sealed record EggEncryption(byte Method, byte[] Header, byte[] Footer) {
  public const byte Standard = 0, Aes128 = 1, Aes256 = 2, Lea128 = 5, Lea256 = 6;

  public static EggEncryption Parse(ReadOnlySpan<byte> field) {
    if (field.IsEmpty)
      throw new InvalidDataException("Empty EGG encryption field.");
    var method = field[0];
    var (headerLength, footerLength) = method switch {
      Standard => (12, 4),
      Aes128 or Lea128 => (10, 10),
      Aes256 or Lea256 => (18, 10),
      _ => throw new NotSupportedException($"EGG encryption method {method} is unknown."),
    };
    if (field.Length < 1 + headerLength + footerLength)
      throw new InvalidDataException("Truncated EGG encryption field.");
    return new(method, field.Slice(1, headerLength).ToArray(), field.Slice(1 + headerLength, footerLength).ToArray());
  }

  public string MethodName => this.Method switch {
    Standard => "ZIP 2.0",
    Aes128 => "AES-128",
    Aes256 => "AES-256",
    Lea128 => "LEA-128",
    Lea256 => "LEA-256",
    _ => $"method {this.Method}",
  };

  /// <summary>Decrypts a member's packed bytes; throws <see cref="InvalidDataException"/> on a wrong password or MAC.</summary>
  public byte[] Decrypt(byte[] data, string password) => this.Method switch {
    Standard => DecryptStandard(data, password),
    Aes128 or Aes256 => DecryptWinZipStyle(data, password, this.Method == Aes256 ? 256 : 128, lea: false),
    Lea128 or Lea256 => DecryptWinZipStyle(data, password, this.Method == Lea256 ? 256 : 128, lea: true),
    _ => throw new NotSupportedException($"EGG encryption method {this.Method} is unknown."),
  };

  // ── ZIP 2.0 ───────────────────────────────────────────────────────────

  private byte[] DecryptStandard(byte[] data, string password) {
    var keys = new ZipKeys();
    foreach (var c in password)
      keys.Update((byte)c);
    byte last = 0;
    foreach (var b in this.Header) {
      last = (byte)(b ^ keys.Stream());
      keys.Update(last);
    }
    if (last != this.Footer[^1])
      throw new InvalidDataException("Wrong EGG password.");
    var output = (byte[])data.Clone();
    for (var i = 0; i < output.Length; ++i) {
      var p = (byte)(output[i] ^ keys.Stream());
      keys.Update(p);
      output[i] = p;
    }
    return output;
  }

  private sealed class ZipKeys {
    private static readonly uint[] Table = [.. Enumerable.Range(0, 256).Select(i => {
      var c = (uint)i;
      for (var j = 0; j < 8; ++j)
        c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
      return c;
    })];
    private uint _k0 = 305419896, _k1 = 591751049, _k2 = 878082192;

    public void Update(byte b) {
      this._k0 = Table[(this._k0 ^ b) & 0xFF] ^ (this._k0 >> 8);
      this._k1 = (this._k1 + (this._k0 & 0xFF)) * 134775813 + 1;
      this._k2 = Table[(this._k2 ^ (this._k1 >> 24)) & 0xFF] ^ (this._k2 >> 8);
    }

    public byte Stream() {
      var t = (this._k2 | 2) & 0xFFFF;
      return (byte)((t * (t ^ 1)) >> 8);
    }
  }

  // ── AES / LEA (WinZip-style key derivation, CTR, HMAC-SHA1) ───────────

  private byte[] DecryptWinZipStyle(byte[] data, string password, int bits, bool lea) {
    var saltLength = bits == 256 ? 16 : 8;
    var keyLength = bits / 8;
    var macLength = lea ? (bits == 256 ? 32 : 16) : keyLength;
    var salt = this.Header.AsSpan(0, saltLength);
    var storedCheck = this.Header.AsSpan(saltLength, 2);
    var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 1000, HashAlgorithmName.SHA1, keyLength + macLength + 2);
    var key = derived.AsSpan(0, keyLength);
    var macKey = derived.AsSpan(keyLength, macLength);
    var check = derived.AsSpan(keyLength + macLength, 2);

    var valid = lea
      ? check.SequenceEqual(storedCheck)
      : (short)(check[0] + check[1] * bits) == (short)(storedCheck[0] + storedCheck[1] * bits);
    if (!valid)
      throw new InvalidDataException("Wrong EGG password.");

    var mac = HMACSHA1.HashData(macKey, data);
    if (!mac.AsSpan(0, 10).SequenceEqual(this.Footer))
      throw new InvalidDataException("EGG member failed its authentication code (wrong password or corrupt data).");

    return lea ? LeaCtr(data, key.ToArray()) : AesCtrLittleEndian(data, key.ToArray());
  }

  /// <summary>AES in CTR mode with WinZip's counter: a little-endian block number starting at 1.</summary>
  private static byte[] AesCtrLittleEndian(byte[] data, byte[] key) {
    using var aes = Aes.Create();
    aes.Key = key;
    var output = new byte[data.Length];
    var counter = new byte[16];
    var stream = new byte[16];
    ulong block = 1;
    for (var pos = 0; pos < data.Length; pos += 16) {
      BinaryPrimitives.WriteUInt64LittleEndian(counter, block++);
      aes.EncryptEcb(counter, stream, PaddingMode.None);
      var n = Math.Min(16, data.Length - pos);
      for (var i = 0; i < n; ++i)
        output[pos + i] = (byte)(data[pos + i] ^ stream[i]);
    }
    return output;
  }

  /// <summary>LEA in CTR mode: zero IV, big-endian counter increment.</summary>
  private static byte[] LeaCtr(byte[] data, byte[] key) {
    var cipher = new Lea(key);
    var output = new byte[data.Length];
    var counter = new byte[16];
    var stream = new byte[16];
    for (var pos = 0; pos < data.Length; pos += 16) {
      cipher.EncryptBlock(counter, stream);
      for (var i = 15; i >= 0; --i)
        if (++counter[i] != 0)
          break;
      var n = Math.Min(16, data.Length - pos);
      for (var i = 0; i < n; ++i)
        output[pos + i] = (byte)(data[pos + i] ^ stream[i]);
    }
    return output;
  }
}

/// <summary>The LEA block cipher (128-bit blocks; 128-, 192- or 256-bit keys).</summary>
internal sealed class Lea {
  private static readonly uint[] Delta = [0xc3efe9db, 0x44626b02, 0x79e27c8a, 0x78df30ec, 0x715ea49e, 0xc785da0a, 0xe04ef22a, 0xe5c40957];
  private readonly uint[,] _rk;
  private readonly int _rounds;

  private static uint Rol(uint v, int n) => BitOperations.RotateLeft(v, n);
  private static uint Ror(uint v, int n) => BitOperations.RotateRight(v, n);

  public Lea(byte[] key) {
    if (key.Length is not (16 or 24 or 32))
      throw new ArgumentException("LEA keys are 16, 24 or 32 bytes.", nameof(key));
    this._rounds = (key.Length >> 1) + 16;
    this._rk = new uint[this._rounds, 6];
    var t = new uint[8];
    for (var i = 0; i < key.Length / 4; ++i)
      t[i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
    switch (key.Length) {
      case 16:
        for (var i = 0; i < 24; ++i) {
          var temp = Rol(Delta[i & 3], i);
          this._rk[i, 0] = t[0] = Rol(t[0] + temp, 1);
          this._rk[i, 1] = this._rk[i, 3] = this._rk[i, 5] = t[1] = Rol(t[1] + Rol(temp, 1), 3);
          this._rk[i, 2] = t[2] = Rol(t[2] + Rol(temp, 2), 6);
          this._rk[i, 4] = t[3] = Rol(t[3] + Rol(temp, 3), 11);
        }
        break;
      case 24:
        for (var i = 0; i < 28; ++i) {
          var temp = Rol(Delta[i % 6], i);
          this._rk[i, 0] = t[0] = Rol(t[0] + temp, 1);
          this._rk[i, 1] = t[1] = Rol(t[1] + Rol(temp, 1), 3);
          this._rk[i, 2] = t[2] = Rol(t[2] + Rol(temp, 2), 6);
          this._rk[i, 3] = t[3] = Rol(t[3] + Rol(temp, 3), 11);
          this._rk[i, 4] = t[4] = Rol(t[4] + Rol(temp, 4), 13);
          this._rk[i, 5] = t[5] = Rol(t[5] + Rol(temp, 5), 17);
        }
        break;
      default:
        for (var i = 0; i < 32; ++i) {
          var temp = Rol(Delta[i & 7], i & 0x1f);
          this._rk[i, 0] = t[(6 * i + 0) & 7] = Rol(t[(6 * i + 0) & 7] + temp, 1);
          this._rk[i, 1] = t[(6 * i + 1) & 7] = Rol(t[(6 * i + 1) & 7] + Rol(temp, 1), 3);
          this._rk[i, 2] = t[(6 * i + 2) & 7] = Rol(t[(6 * i + 2) & 7] + Rol(temp, 2), 6);
          this._rk[i, 3] = t[(6 * i + 3) & 7] = Rol(t[(6 * i + 3) & 7] + Rol(temp, 3), 11);
          this._rk[i, 4] = t[(6 * i + 4) & 7] = Rol(t[(6 * i + 4) & 7] + Rol(temp, 4), 13);
          this._rk[i, 5] = t[(6 * i + 5) & 7] = Rol(t[(6 * i + 5) & 7] + Rol(temp, 5), 17);
        }
        break;
    }
  }

  public void EncryptBlock(ReadOnlySpan<byte> input, Span<byte> output) {
    var b0 = BinaryPrimitives.ReadUInt32LittleEndian(input);
    var b1 = BinaryPrimitives.ReadUInt32LittleEndian(input[4..]);
    var b2 = BinaryPrimitives.ReadUInt32LittleEndian(input[8..]);
    var b3 = BinaryPrimitives.ReadUInt32LittleEndian(input[12..]);
    for (var i = 0; i < this._rounds; i += 4) {
      b3 = Ror((b2 ^ this._rk[i, 4]) + (b3 ^ this._rk[i, 5]), 3);
      b2 = Ror((b1 ^ this._rk[i, 2]) + (b2 ^ this._rk[i, 3]), 5);
      b1 = Rol((b0 ^ this._rk[i, 0]) + (b1 ^ this._rk[i, 1]), 9);
      b0 = Ror((b3 ^ this._rk[i + 1, 4]) + (b0 ^ this._rk[i + 1, 5]), 3);
      b3 = Ror((b2 ^ this._rk[i + 1, 2]) + (b3 ^ this._rk[i + 1, 3]), 5);
      b2 = Rol((b1 ^ this._rk[i + 1, 0]) + (b2 ^ this._rk[i + 1, 1]), 9);
      b1 = Ror((b0 ^ this._rk[i + 2, 4]) + (b1 ^ this._rk[i + 2, 5]), 3);
      b0 = Ror((b3 ^ this._rk[i + 2, 2]) + (b0 ^ this._rk[i + 2, 3]), 5);
      b3 = Rol((b2 ^ this._rk[i + 2, 0]) + (b3 ^ this._rk[i + 2, 1]), 9);
      b2 = Ror((b1 ^ this._rk[i + 3, 4]) + (b2 ^ this._rk[i + 3, 5]), 3);
      b1 = Ror((b0 ^ this._rk[i + 3, 2]) + (b1 ^ this._rk[i + 3, 3]), 5);
      b0 = Rol((b3 ^ this._rk[i + 3, 0]) + (b0 ^ this._rk[i + 3, 1]), 9);
    }
    BinaryPrimitives.WriteUInt32LittleEndian(output, b0);
    BinaryPrimitives.WriteUInt32LittleEndian(output[4..], b1);
    BinaryPrimitives.WriteUInt32LittleEndian(output[8..], b2);
    BinaryPrimitives.WriteUInt32LittleEndian(output[12..], b3);
  }
}
