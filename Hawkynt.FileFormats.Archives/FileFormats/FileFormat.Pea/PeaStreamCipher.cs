using System.Security.Cryptography;
using System.Text;
using Compression.Core.Crypto;
using Compression.Registry;
using Hawkynt.Algorithms.Hashing;

namespace FileFormat.Pea;

/// <summary>
/// PEA stream encryption: the authenticated-encryption stream controls of PeaZip's pea, which
/// encrypt everything after a 16-byte crypto subheader up to the end-of-archive trigger and end
/// the stream with a 16-byte tag in place of a stream check.
/// </summary>
/// <remarks>
/// Converted from PeaZip's <c>pea.pas</c> / <c>pea_utils.pas</c> (Giorgio Tani, LGPL-3.0) and the
/// file-crypt units <c>fcrypta</c>, <c>fcryptt</c>, <c>fcrypts</c>, <c>fcaes256</c>, <c>fctf256</c>,
/// <c>fcsp256</c> (Wolfgang Ehrhardt, zlib licence; Giorgio Tani) — see
/// THIRD-PARTY-NOTICE.PeaZip.txt. Measured against archives PeaZip 11.4's pea writes.
/// <list type="bullet">
/// <item>Keying material: the password bytes, the 10-byte archive header, the 10-byte stream header and two zero bytes.</item>
/// <item>Subheader: two zero bytes, a 12-byte salt, a 2-byte password verifier.</item>
/// <item>PBKDF2 with 1000 iterations over HMAC-SHA-1 (128-bit kinds) or HMAC-Whirlpool (256-bit
/// kinds) gives the cipher key, a 16/32-byte EAX nonce (or HMAC key) and the verifier.</item>
/// <item>EAX over AES, Twofish or Serpent without header data; HMAC is AES-128 CTR from a zero
/// counter with HMAC-SHA-1 of the ciphertext, cut to 16 bytes.</item>
/// </list>
/// </remarks>
internal sealed class PeaStreamCipher {
  /// <summary>Size of the crypto subheader after the stream header.</summary>
  public const int SubheaderSize = 16;
  /// <summary>Size of the authentication tag that ends the stream.</summary>
  public const int TagSize = 16;
  private const int SaltSize = 12;
  private const int Iterations = 1000;

  private readonly Eax? _eax;
  private readonly AesCtr? _ctr;
  private readonly IncrementalHash? _hmac;

  private PeaStreamCipher(Eax eax) => this._eax = eax;
  private PeaStreamCipher(AesCtr ctr, IncrementalHash hmac) => (this._ctr, this._hmac) = (ctr, hmac);

  /// <summary>Whether a stream control id is one of the encrypting kinds this class handles.</summary>
  public static bool Handles(byte algo) => algo is 0x30 or 0x31 or 0x32 or 0x33 or 0x41 or 0x42 or 0x43;

  /// <summary>Whether a stream control id encrypts (including the cascades this class refuses).</summary>
  public static bool Encrypts(byte algo) => algo is >= 0x30 and <= 0x33 or >= 0x41 and <= 0x4C;

  /// <summary>Opens the stream for decryption, after checking the password verifier.</summary>
  /// <exception cref="ArchivePasswordException">No password, or the verifier does not match it.</exception>
  public static PeaStreamCipher Open(byte algo, string? password, ReadOnlySpan<byte> headers, ReadOnlySpan<byte> subheader) {
    if (string.IsNullOrEmpty(password))
      throw new ArchivePasswordException("This PEA archive is encrypted; give its password.", passwordWasSupplied: false);
    var key = Derive(algo, password, headers, subheader.Slice(2, SaltSize));
    var verifier = key.AsSpan(key.Length - 2);
    if (!verifier.SequenceEqual(subheader.Slice(14, 2)))
      throw new ArchivePasswordException("Wrong password for this PEA archive.", passwordWasSupplied: true);
    return Start(algo, key);
  }

  /// <summary>Starts a new encrypted stream with a fresh salt, returning the subheader to write.</summary>
  public static PeaStreamCipher Create(byte algo, string password, ReadOnlySpan<byte> headers, out byte[] subheader) {
    ArgumentException.ThrowIfNullOrEmpty(password);
    var salt = RandomNumberGenerator.GetBytes(SaltSize);
    var key = Derive(algo, password, headers, salt);
    subheader = new byte[SubheaderSize];
    salt.CopyTo(subheader, 2);
    key.AsSpan(key.Length - 2).CopyTo(subheader.AsSpan(14));
    return Start(algo, key);
  }

  private static byte[] Derive(byte algo, string password, ReadOnlySpan<byte> headers, ReadOnlySpan<byte> salt) {
    if (!Handles(algo)) throw new NotSupportedException($"PEA stream encryption 0x{algo:X2} (a cascade) is not supported.");
    var pw = Encoding.UTF8.GetBytes(password);
    var material = new byte[pw.Length + 22];
    pw.CopyTo(material, 0);
    headers[..20].CopyTo(material.AsSpan(pw.Length));
    return algo >= 0x41
      ? KeyDerivation.Pbkdf2(static d => Whirlpool.Compute(d), 64, material, salt, Iterations, 32 + 32 + 2)
      : Rfc2898DeriveBytes.Pbkdf2(material, salt, Iterations, HashAlgorithmName.SHA1, 16 + 16 + 2);
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

  /// <summary>Encrypts in place.</summary>
  public void Encrypt(Span<byte> data) {
    if (this._eax is { } eax) { eax.Encrypt(data); return; }
    this._ctr!.Apply(data);
    this._hmac!.AppendData(data);
  }

  /// <summary>Decrypts in place.</summary>
  public void Decrypt(Span<byte> data) {
    if (this._eax is { } eax) { eax.Decrypt(data); return; }
    this._hmac!.AppendData(data);
    this._ctr!.Apply(data);
  }

  /// <summary>The 16-byte tag over the whole stream.</summary>
  public byte[] Tag() => this._eax is { } eax ? eax.Tag() : this._hmac!.GetHashAndReset().AsSpan(0, TagSize).ToArray();

  /// <summary>AES in CTR mode from a zero counter block, big-endian increment over all 128 bits.</summary>
  private sealed class AesCtr(ReadOnlySpan<byte> key) {
    private readonly Aes _aes = CreateAes(key);
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keyStream = new byte[16];
    private int _used = 16;

    private static Aes CreateAes(ReadOnlySpan<byte> key) {
      var aes = Aes.Create();
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
