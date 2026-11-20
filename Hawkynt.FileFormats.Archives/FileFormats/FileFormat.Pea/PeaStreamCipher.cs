using System.Security.Cryptography;
using System.Text;
using Compression.Core.Crypto;
using Compression.Registry;
using FileFormat.Zpaq;
using Hawkynt.Algorithms.Hashing;

namespace FileFormat.Pea;

/// <summary>
/// PEA stream encryption: the authenticated-encryption stream controls of PeaZip's pea, which
/// encrypt everything after a crypto subheader up to the end-of-archive trigger and end the
/// stream with a tag in place of a stream check.
/// </summary>
/// <remarks>
/// Converted from PeaZip's <c>pea.pas</c> / <c>pea_utils.pas</c> (Giorgio Tani, LGPL-3.0) and the
/// file-crypt units <c>fcrypta</c>, <c>fcryptt</c>, <c>fcrypts</c>, <c>fcaes256</c>, <c>fctf256</c>,
/// <c>fcsp256</c> (Wolfgang Ehrhardt, zlib licence; Giorgio Tani) — see
/// THIRD-PARTY-NOTICE.PeaZip.txt. Measured against archives PeaZip 11.4's pea writes.
/// <list type="bullet">
/// <item>Keying material: the password bytes, the 10-byte archive header, the 10-byte stream header and two zero bytes.</item>
/// <item>Subheader: two zero bytes, a 12-byte salt, a 2-byte password verifier.</item>
/// <item>Single ciphers: PBKDF2 with 1000 iterations over HMAC-SHA-1 (128-bit kinds) or
/// HMAC-Whirlpool (256-bit kinds) gives the cipher key, a 16/32-byte EAX nonce (or HMAC key) and
/// the verifier. EAX runs over AES, Twofish or Serpent without header data; HMAC is AES-128 CTR
/// from a zero counter with HMAC-SHA-1 of the ciphertext, cut to 16 bytes.</item>
/// <item>Triple cascades (TRI/SRI/HRI × ATS/TSA/SAT): three 256-bit EAX ciphers in the named order
/// (A = AES, T = Twofish, S = Serpent), each with its own subheader and key; the second and third
/// cipher's keying material is the first's with every byte i XORed with (length + i) and the
/// name's fifth letter, resp. (length XOR i) and its sixth. The key derivation depends on the
/// family and on the strength n in archive header byte 9 (see <see cref="DeriveCascade"/>).
/// Only the last subheader carries a verifier, the XOR of the three. The ciphertext starts with
/// a padding byte p and p more bytes; the tag is SHA3-384 of the three EAX tags.</item>
/// </list>
/// </remarks>
internal sealed class PeaStreamCipher {
  private const int SaltSize = 12;
  private const int SubheaderUnit = 16;
  private const int Iterations = 1000;

  private readonly Eax[] _eax;
  private readonly AesCtr? _ctr;
  private readonly IncrementalHash? _hmac;

  private PeaStreamCipher(params Eax[] eax) => this._eax = eax;
  private PeaStreamCipher(AesCtr ctr, IncrementalHash hmac) => (this._eax, this._ctr, this._hmac) = ([], ctr, hmac);

  /// <summary>Whether a stream control id encrypts.</summary>
  public static bool Encrypts(byte algo) => algo is >= 0x30 and <= 0x33 or >= 0x41 and <= 0x4C;

  /// <summary>Whether a stream control id is a triple cascade.</summary>
  public static bool IsCascade(byte algo) => algo is >= 0x44 and <= 0x4C;

  /// <summary>Size of the crypto subheader(s) after the stream header.</summary>
  public static int SubheaderSize(byte algo) => IsCascade(algo) ? 3 * SubheaderUnit : SubheaderUnit;

  /// <summary>Size of the authentication tag that ends the stream.</summary>
  public static int TagSize(byte algo) => IsCascade(algo) ? 48 : 16;

  /// <summary>The cipher order (letters A, T, S) and key derivation family of a cascade id.</summary>
  private static readonly string[] CascadeOrders = ["ATS", "TSA", "SAT"];
  private static readonly string[] CascadeKdfs = ["pbkdf2", "scrypt", "hybrid"];

  private static (string Order, string Kdf) Cascade(byte algo) {
    var i = algo - 0x44;
    return (CascadeOrders[i % 3], CascadeKdfs[i / 3]);
  }

  /// <summary>Opens the stream for decryption, after checking the password verifier.</summary>
  /// <param name="algo">The stream control id.</param>
  /// <param name="strength">Archive header byte 9 (pea's iteration level for the cascades).</param>
  /// <param name="password">The password.</param>
  /// <param name="headers">The archive and stream headers (20 bytes).</param>
  /// <param name="subheader">The crypto subheader(s).</param>
  /// <exception cref="ArchivePasswordException">No password, or the verifier does not match it.</exception>
  public static PeaStreamCipher Open(byte algo, int strength, string? password, ReadOnlySpan<byte> headers, ReadOnlySpan<byte> subheader) {
    if (string.IsNullOrEmpty(password))
      throw new ArchivePasswordException("This PEA archive is encrypted; give its password.", passwordWasSupplied: false);
    var material = Material(password, headers);
    if (IsCascade(algo)) {
      var keys = CascadeKeys(algo, strength, material, subheader);
      if (!Verifier(keys).AsSpan().SequenceEqual(subheader.Slice(2 * SubheaderUnit + 14, 2)))
        throw new ArchivePasswordException("Wrong password for this PEA archive.", passwordWasSupplied: true);
      return StartCascade(algo, keys);
    }
    var key = Derive(algo, material, subheader.Slice(2, SaltSize));
    if (!key.AsSpan(key.Length - 2).SequenceEqual(subheader.Slice(14, 2)))
      throw new ArchivePasswordException("Wrong password for this PEA archive.", passwordWasSupplied: true);
    return Start(algo, key);
  }

  /// <summary>Starts a new encrypted stream with fresh salts, returning the subheader(s) to write.</summary>
  public static PeaStreamCipher Create(byte algo, int strength, string password, ReadOnlySpan<byte> headers, out byte[] subheader) {
    ArgumentException.ThrowIfNullOrEmpty(password);
    var material = Material(password, headers);
    subheader = new byte[SubheaderSize(algo)];
    for (var i = 0; i < subheader.Length; i += SubheaderUnit)
      RandomNumberGenerator.Fill(subheader.AsSpan(i + 2, SaltSize));
    if (IsCascade(algo)) {
      var keys = CascadeKeys(algo, strength, material, subheader);
      Verifier(keys).CopyTo(subheader, 2 * SubheaderUnit + 14);
      return StartCascade(algo, keys);
    }
    var key = Derive(algo, material, subheader.AsSpan(2, SaltSize));
    key.AsSpan(key.Length - 2).CopyTo(subheader.AsSpan(14));
    return Start(algo, key);
  }

  private static byte[] Material(string password, ReadOnlySpan<byte> headers) {
    var pw = Encoding.UTF8.GetBytes(password);
    var material = new byte[pw.Length + 22];
    pw.CopyTo(material, 0);
    headers[..20].CopyTo(material.AsSpan(pw.Length));
    return material;
  }

  private static byte[] Derive(byte algo, byte[] material, ReadOnlySpan<byte> salt) => algo >= 0x41
    ? WhirlpoolPbkdf2(material, salt, Iterations, 32 + 32 + 2)
    : Rfc2898DeriveBytes.Pbkdf2(material, salt, Iterations, HashAlgorithmName.SHA1, 16 + 16 + 2);

  private static byte[][] CascadeKeys(byte algo, int strength, byte[] material, ReadOnlySpan<byte> subheader) {
    var (order, kdf) = Cascade(algo);
    var keys = new byte[3][];
    for (var k = 0; k < 3; ++k) {
      var m = (byte[])material.Clone();
      for (var i = 0; i < m.Length; ++i)
        m[i] = k switch {
          0 => m[i],
          1 => (byte)(m[i] ^ (m.Length + i) ^ order[1]),
          _ => (byte)(m[i] ^ (m.Length ^ i) ^ order[2]),
        };
      keys[k] = DeriveCascade(order[k], kdf, strength, m, subheader.Slice(k * SubheaderUnit + 2, SaltSize));
    }
    return keys;
  }

  /// <summary>
  /// A cascade cipher's 66 key bytes. For strength n: PBKDF2 runs n·100000 + 25000 (AES, over
  /// Whirlpool), + 50000 (Twofish, SHA-512) or + 75000 (Serpent, SHA3-512) iterations; scrypt
  /// takes N = 64 Ki, 128 Ki, 256 Ki, 512 Ki for n = 0…3 and 1 Mi beyond, r = 8, and p = 2, 4, 8
  /// for n = 5, 6, 7 (else 1). The hybrid family uses scrypt for AES, scrypt with N/2 and r = 16
  /// for Twofish, and PBKDF2-SHA3-512 with 75000, 275000, 575000, 1075000, 2075000, 5075000,
  /// 10075000, 25075000 iterations for Serpent.
  /// </summary>
  internal static byte[] DeriveCascade(char cipher, string kdf, int strength, byte[] material, ReadOnlySpan<byte> salt) {
    const int length = 66;
    var memory = strength switch { 1 => 128 << 10, 2 => 256 << 10, 3 => 512 << 10, >= 4 and <= 7 => 1 << 20, _ => 64 << 10 };
    var parallel = strength switch { 5 => 2, 6 => 4, 7 => 8, _ => 1 };
    var iterations = strength * 100000 + cipher switch { 'A' => 25000, 'T' => 50000, _ => 75000 };
    return (cipher, kdf) switch {
      ('A', "pbkdf2") => WhirlpoolPbkdf2(material, salt, iterations, length),
      ('A', _) => ZpaqCrypto.Scrypt(material, salt, memory, 8, parallel, length),
      ('T', "pbkdf2") => Rfc2898DeriveBytes.Pbkdf2(material, salt, iterations, HashAlgorithmName.SHA512, length),
      ('T', "scrypt") => ZpaqCrypto.Scrypt(material, salt, memory, 8, parallel, length),
      ('T', _) => ZpaqCrypto.Scrypt(material, salt, memory / 2, 16, parallel, length),
      ('S', "scrypt") => ZpaqCrypto.Scrypt(material, salt, memory, 8, parallel, length),
      ('S', "hybrid") => Sha3Pbkdf2(material, salt, strength switch {
        1 => 275000, 2 => 575000, 3 => 1075000, 4 => 2075000, 5 => 5075000, 6 => 10075000, 7 => 25075000, _ => 75000,
      }, length),
      _ => Sha3Pbkdf2(material, salt, iterations, length),
    };
  }

  // HMAC over SHA3-512 takes the sponge rate, 72 bytes, as its block size; the HMAC pads are hashed once.
  private static byte[] Sha3Pbkdf2(byte[] material, ReadOnlySpan<byte> salt, int iterations, int length)
    => Pbkdf2.Derive(KeccakHasher.Sha3_512(), material, salt, iterations, length);

  private static byte[] WhirlpoolPbkdf2(byte[] material, ReadOnlySpan<byte> salt, int iterations, int length)
    => Pbkdf2.Derive(new WhirlpoolHasher(), material, salt, iterations, length);

  private static byte[] Verifier(byte[][] keys)
    => [(byte)(keys[0][64] ^ keys[1][64] ^ keys[2][64]), (byte)(keys[0][65] ^ keys[1][65] ^ keys[2][65])];

  private static PeaStreamCipher StartCascade(byte algo, byte[][] keys) {
    var order = Cascade(algo).Order;
    var eax = new Eax[3];
    for (var k = 0; k < 3; ++k) {
      var key = keys[k].AsSpan(0, 32);
      var nonce = keys[k].AsSpan(32, 32);
      eax[k] = order[k] switch {
        'A' => Eax.WithAes(key, nonce),
        'T' => Eax.WithTwofish(key, nonce),
        _ => Eax.WithSerpent(key, nonce),
      };
    }
    return new PeaStreamCipher(eax);
  }

  private static PeaStreamCipher Start(byte algo, byte[] key) {
    var half = algo >= 0x41 ? 32 : 16;
    var k = key.AsSpan(0, half);
    var n = key.AsSpan(half, half);
    return algo switch {
      0x30 => new PeaStreamCipher(new AesCtr(k), IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, n)),
      0x31 or 0x41 => new PeaStreamCipher(Eax.WithAes(k, n)),
      0x32 or 0x42 => new PeaStreamCipher(Eax.WithTwofish(k, n)),
      _ => new PeaStreamCipher(Eax.WithSerpent(k, n)),
    };
  }

  /// <summary>Encrypts in place: every cipher of a cascade in order.</summary>
  public void Encrypt(Span<byte> data) {
    if (this._eax.Length > 0) {
      foreach (var eax in this._eax) eax.Encrypt(data);
      return;
    }
    this._ctr!.Apply(data);
    this._hmac!.AppendData(data);
  }

  /// <summary>Decrypts in place: every cipher of a cascade in reverse order.</summary>
  public void Decrypt(Span<byte> data) {
    if (this._eax.Length > 0) {
      for (var k = this._eax.Length - 1; k >= 0; --k) this._eax[k].Decrypt(data);
      return;
    }
    this._hmac!.AppendData(data);
    this._ctr!.Apply(data);
  }

  /// <summary>The tag over the whole stream: the EAX tag, the HMAC cut to 16 bytes, or SHA3-384 of a cascade's three tags.</summary>
  public byte[] Tag() => this._eax.Length switch {
    0 => this._hmac!.GetHashAndReset().AsSpan(0, 16).ToArray(),
    1 => this._eax[0].Tag(),
    _ => Sha3.Compute384([.. this._eax.SelectMany(static e => e.Tag())]),
  };

  /// <summary>AES in CTR mode from a zero counter block, big-endian increment over all 128 bits.</summary>
  private sealed class AesCtr(ReadOnlySpan<byte> key) {
    private readonly Aes _aes = CreateAes(key);
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keyStream = new byte[16];
    private int _used = 16;

    private static Aes CreateAes(ReadOnlySpan<byte> key) {
      var aes = ManagedAes.Create();
      aes.Key = key.ToArray();
      return aes;
    }

    public void Apply(Span<byte> data) {
      for (var i = 0; i < data.Length; ++i) {
        if (this._used == 16) {
          this._aes.EncryptEcb(this._counter, this._keyStream, PaddingMode.None);
          for (var j = 15; j >= 0 && ++this._counter[j] == 0; --j) { }
          this._used = 0;
        }
        data[i] ^= this._keyStream[this._used++];
      }
    }
  }
}
