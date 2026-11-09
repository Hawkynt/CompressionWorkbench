using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Luks;
using Hawkynt.Algorithms.Hashing;

namespace FileFormat.DmCrypt;

/// <summary>
/// The parameters of a dm-crypt plain mapping — everything <c>cryptsetup open --type plain</c>
/// is told, because a plain volume stores none of it. Defaults are cryptsetup's plain-mode defaults.
/// </summary>
/// <param name="Cipher">Cipher spec, <c>cipher-mode-iv</c> (<c>aes-xts-plain64</c>, <c>aes-cbc-essiv:sha256</c>, …).</param>
/// <param name="Hash">Passphrase hash (<c>sha1</c>, <c>sha256</c>, <c>sha512</c>, <c>ripemd160</c>, …, or <c>plain</c>), optionally <c>:bytes</c> to hash only that much of the key.</param>
/// <param name="KeyBits">Key size in bits.</param>
/// <param name="OffsetSectors">Start of the data in the device, in 512-byte sectors.</param>
/// <param name="SkipSectors">IV offset: the IV sector number of the first data sector.</param>
/// <param name="SizeSectors">Data length in 512-byte sectors; <see langword="null"/> for the rest of the device.</param>
public sealed record DmCryptPlainParameters(
  string Cipher = "aes-xts-plain64", string Hash = "sha256", int KeyBits = 256,
  long OffsetSectors = 0, long SkipSectors = 0, long? SizeSectors = null) {

  public const string CipherName = "Cipher", HashName = "Hash", KeySizeName = "KeySize", OffsetName = "Offset", SkipName = "Skip", SizeName = "Size";

  /// <summary>Reads the parameters by name, each falling back to its default.</summary>
  public static DmCryptPlainParameters From(Func<string, string?> get) {
    ArgumentNullException.ThrowIfNull(get);
    var d = new DmCryptPlainParameters();
    return new(
      get(CipherName) ?? d.Cipher,
      get(HashName) ?? d.Hash,
      Number(get(KeySizeName), KeySizeName) is { } k ? checked((int)k) : d.KeyBits,
      Number(get(OffsetName), OffsetName) ?? d.OffsetSectors,
      Number(get(SkipName), SkipName) ?? d.SkipSectors,
      Number(get(SizeName), SizeName));
  }

  private static long? Number(string? text, string name)
    => text is null ? null
      : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v
      : throw new ArgumentException($"dm-crypt plain: '{name}' must be a non-negative integer, not '{text}'.");

  /// <summary>Splits <see cref="Cipher"/> into the cipher and the <c>mode-iv</c> part LUKS also uses.</summary>
  public (string Cipher, string Mode) SplitCipher() {
    var dash = this.Cipher.IndexOf('-');
    if (dash <= 0 || dash == this.Cipher.Length - 1)
      throw new ArgumentException($"dm-crypt plain: '{this.Cipher}' is not a cipher-mode-iv spec.");
    return (this.Cipher[..dash], this.Cipher[(dash + 1)..]);
  }
}

/// <summary>
/// dm-crypt plain mode: a headerless volume whose key is the hashed passphrase. Implemented from
/// the behaviour of <c>cryptsetup open --type plain</c>, as written down in
/// <c>docs/DMCRYPT-PLAIN-ON-DISK.md</c>.
/// </summary>
public static class DmCryptPlainVolume {
  public const int SectorSize = 512;

  /// <summary>
  /// The volume key for <paramref name="passphrase"/>: the hash of the passphrase, extended when
  /// the key is longer than one digest by hashing again with one more <c>'A'</c> prepended per
  /// round; with <c>hash:n</c> only the first <c>n</c> key bytes are hashed and the rest are zero;
  /// <c>plain</c> takes the passphrase bytes themselves.
  /// </summary>
  public static byte[] DeriveKey(string passphrase, string hashSpec, int keyBytes) {
    ArgumentNullException.ThrowIfNull(passphrase);
    ArgumentNullException.ThrowIfNull(hashSpec);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyBytes);
    var name = hashSpec;
    var hashed = keyBytes;
    if (hashSpec.IndexOf(':') is var colon and >= 0) {
      name = hashSpec[..colon];
      if (!int.TryParse(hashSpec[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out hashed) || hashed > keyBytes)
        throw new ArgumentException($"dm-crypt plain: hash length in '{hashSpec}' must be a number no larger than the {keyBytes}-byte key.", nameof(hashSpec));
    }
    var pass = Encoding.UTF8.GetBytes(passphrase);
    var key = new byte[keyBytes];
    if (name.Equals("plain", StringComparison.OrdinalIgnoreCase)) {
      if (pass.Length < hashed)
        throw new ArgumentException($"dm-crypt plain: with hash 'plain' the passphrase must be at least {hashed} bytes long.", nameof(passphrase));
      pass.AsSpan(0, hashed).CopyTo(key);
      return key;
    }
    var hash = HashFunction(name);
    for (int round = 0, done = 0; done < hashed; ++round) {
      var input = new byte[round + pass.Length];
      input.AsSpan(0, round).Fill((byte)'A');
      pass.CopyTo(input, round);
      var digest = hash(input);
      var n = Math.Min(digest.Length, hashed - done);
      digest.AsSpan(0, n).CopyTo(key.AsSpan(done));
      done += n;
    }
    return key;
  }

  /// <summary>Whether <paramref name="name"/> is a passphrase hash this implementation knows.</summary>
  public static bool IsSupportedHash(string name) {
    try {
      _ = HashFunction(name.Split(':')[0]);
      return true;
    } catch (NotSupportedException) {
      return name.Split(':')[0].Equals("plain", StringComparison.OrdinalIgnoreCase);
    }
  }

  private static Func<byte[], byte[]> HashFunction(string name) => name.ToLowerInvariant() switch {
    "sha1" => SHA1.HashData,
    "sha256" => SHA256.HashData,
    "sha384" => SHA384.HashData,
    "sha512" => SHA512.HashData,
    "ripemd160" or "rmd160" => static d => Ripemd160.Compute(d),
    "whirlpool" => static d => Whirlpool.Compute(d),
    _ => throw new NotSupportedException($"dm-crypt plain: passphrase hash '{name}' is not supported (sha1, sha256, sha384, sha512, ripemd160, whirlpool and plain are)."),
  };

  /// <summary>The decrypted mapping as a block device of 512-byte sectors.</summary>
  public static SectorCipherBlockDevice OpenBlockDevice(Stream stream, string passphrase, DmCryptPlainParameters p, bool writable, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(p);
    var (cipher, mode) = p.SplitCipher();
    if (p.KeyBits % 8 != 0) throw new ArgumentException("dm-crypt plain: the key size must be whole bytes.");
    var key = DeriveKey(passphrase, p.Hash, p.KeyBits / 8);
    try {
      var offset = p.OffsetSectors * SectorSize;
      var available = Math.Max(0, stream.Length - offset) / SectorSize;
      var sectors = p.SizeSectors ?? available;
      if (sectors > available)
        throw new InvalidDataException($"dm-crypt plain: {sectors} sectors at offset {p.OffsetSectors} run past the end of the device.");
      return new SectorCipherBlockDevice(stream, offset, SectorSize, sectors,
        new PlainCipher(new LuksCipher(cipher, mode, key), p.SkipSectors), writable, leaveOpen);
    } finally {
      CryptographicOperations.ZeroMemory(key);
    }
  }

  /// <summary>Writes <paramref name="payload"/> encrypted as a plain mapping would store it, after <c>Offset</c> sectors of random filler.</summary>
  public static void Write(Stream output, ReadOnlySpan<byte> payload, string passphrase, DmCryptPlainParameters p) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(p);
    if (payload.Length % SectorSize != 0)
      throw new ArgumentException("dm-crypt plain payloads must be whole 512-byte sectors.", nameof(payload));
    var (cipher, mode) = p.SplitCipher();
    var key = DeriveKey(passphrase, p.Hash, p.KeyBits / 8);
    using var c = new PlainCipher(new LuksCipher(cipher, mode, key), p.SkipSectors);
    CryptographicOperations.ZeroMemory(key);
    var filler = new byte[64 * 1024];
    for (var left = p.OffsetSectors * SectorSize; left > 0;) {
      var n = (int)Math.Min(filler.Length, left);
      RandomNumberGenerator.Fill(filler.AsSpan(0, n));
      output.Write(filler, 0, n);
      left -= n;
    }
    var buffer = new byte[1 << 20];
    for (var done = 0; done < payload.Length;) {
      var n = Math.Min(buffer.Length, payload.Length - done);
      payload.Slice(done, n).CopyTo(buffer);
      c.Encrypt(buffer.AsSpan(0, n), done / SectorSize);
      output.Write(buffer, 0, n);
      done += n;
    }
    CryptographicOperations.ZeroMemory(buffer);
  }

  /// <summary>Plain mappings number IVs from the skip value, in 512-byte sectors.</summary>
  private sealed class PlainCipher(LuksCipher cipher, long skip) : ISectorCipher {
    public void Decrypt(Span<byte> data, long firstSector) => cipher.Decrypt(data, (ulong)(skip + firstSector));
    public void Encrypt(Span<byte> data, long firstSector) => cipher.Encrypt(data, (ulong)(skip + firstSector));
    public void Dispose() => cipher.Dispose();
  }
}
