using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Compression.Core.Crypto;

/// <summary>
/// A LUKS1 partition header with its eight key slots, after the <i>LUKS On-Disk Format
/// Specification</i> version 1.2.3 (Clemens Fruhwirth): unlocking a slot with a passphrase yields
/// the master key, and <see cref="Create"/> lays out a new header around a master key.
/// </summary>
/// <remarks>
/// Ciphers: AES in <c>xts</c>, <c>cbc</c> or <c>ecb</c> mode with the <c>plain</c>, <c>plain64</c>
/// or <c>essiv:&lt;hash&gt;</c> IV generators; hashes: sha1, sha256, sha384, sha512. Anything else
/// is refused by name.
/// </remarks>
public sealed class Luks1Header {
  /// <summary>Size of the partition header proper (the key slots' material follows it).</summary>
  public const int HeaderSize = 592;

  /// <summary>The LUKS magic: "LUKS" BA BE.</summary>
  public static ReadOnlySpan<byte> Magic => [0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE];

  private const uint SlotActive = 0x00AC71F3;
  private const uint SlotDisabled = 0x0000DEAD;
  private const int DigestSize = 20, SaltSize = 32, SlotCount = 8, SectorSize = 512;

  private Luks1Header(string cipherName, string cipherMode, string hashSpec, uint payloadOffset, int keyBytes,
      byte[] digest, byte[] digestSalt, uint digestIterations, string uuid, Luks1KeySlot[] slots) {
    this.CipherName = cipherName;
    this.CipherMode = cipherMode;
    this.HashSpec = hashSpec;
    this.PayloadOffset = payloadOffset;
    this.KeyBytes = keyBytes;
    this.Digest = digest;
    this.DigestSalt = digestSalt;
    this.DigestIterations = digestIterations;
    this.Uuid = uuid;
    this.Slots = slots;
  }

  /// <summary>Cipher name, e.g. <c>aes</c>.</summary>
  public string CipherName { get; }
  /// <summary>Cipher mode with IV generator, e.g. <c>xts-plain64</c>.</summary>
  public string CipherMode { get; }
  /// <summary>Hash for PBKDF2 and the anti-forensic splitter, e.g. <c>sha256</c>.</summary>
  public string HashSpec { get; }
  /// <summary>Start of the payload in 512-byte sectors from the header.</summary>
  public uint PayloadOffset { get; }
  /// <summary>Master key length in bytes.</summary>
  public int KeyBytes { get; }
  /// <summary>PBKDF2 digest of the master key.</summary>
  public byte[] Digest { get; }
  /// <summary>Salt of <see cref="Digest"/>.</summary>
  public byte[] DigestSalt { get; }
  /// <summary>PBKDF2 iterations of <see cref="Digest"/>.</summary>
  public uint DigestIterations { get; }
  /// <summary>The volume UUID as text.</summary>
  public string Uuid { get; }
  /// <summary>The eight key slots.</summary>
  public IReadOnlyList<Luks1KeySlot> Slots { get; }

  /// <summary>Parses a header from its first 592 bytes.</summary>
  /// <exception cref="InvalidDataException">The bytes are not a LUKS1 header.</exception>
  public static Luks1Header Parse(ReadOnlySpan<byte> header) {
    if (header.Length < HeaderSize || !header[..6].SequenceEqual(Magic))
      throw new InvalidDataException("LUKS: missing LUKS magic.");
    var version = BinaryPrimitives.ReadUInt16BigEndian(header[6..]);
    if (version != 1)
      throw new NotSupportedException($"LUKS: version {version} headers are not supported (LUKS1 only).");
    var keyBytes = BinaryPrimitives.ReadUInt32BigEndian(header[108..]);
    if (keyBytes is 0 or > 128)
      throw new InvalidDataException($"LUKS: implausible master key length {keyBytes}.");
    var slots = new Luks1KeySlot[SlotCount];
    for (var i = 0; i < SlotCount; ++i) {
      var s = header.Slice(208 + 48 * i, 48);
      slots[i] = new Luks1KeySlot(
        BinaryPrimitives.ReadUInt32BigEndian(s) == SlotActive,
        BinaryPrimitives.ReadUInt32BigEndian(s[4..]),
        s.Slice(8, SaltSize).ToArray(),
        BinaryPrimitives.ReadUInt32BigEndian(s[40..]),
        BinaryPrimitives.ReadUInt32BigEndian(s[44..]));
    }
    return new Luks1Header(Text(header.Slice(8, 32)), Text(header.Slice(40, 32)), Text(header.Slice(72, 32)),
      BinaryPrimitives.ReadUInt32BigEndian(header[104..]), (int)keyBytes,
      header.Slice(112, DigestSize).ToArray(), header.Slice(132, SaltSize).ToArray(),
      BinaryPrimitives.ReadUInt32BigEndian(header[164..]), Text(header.Slice(168, 40)), slots);
  }

  /// <summary>
  /// Finds the master key: every active slot is tried with <paramref name="passphrase"/>, its
  /// key material read through <paramref name="readKeyMaterial"/> (offset in bytes from the header, length).
  /// </summary>
  /// <exception cref="CryptographicException">No slot opens with the passphrase.</exception>
  public byte[] Unlock(byte[] passphrase, Func<long, int, byte[]> readKeyMaterial) {
    ArgumentNullException.ThrowIfNull(passphrase);
    ArgumentNullException.ThrowIfNull(readKeyMaterial);
    var hash = HashFor(this.HashSpec);
    using (this.CreateCipher(new byte[this.KeyBytes])) { } // refuses unsupported ciphers before any work
    foreach (var slot in this.Slots) {
      if (!slot.Active)
        continue;
      if (slot.Stripes == 0 || slot.Iterations == 0)
        throw new InvalidDataException("LUKS: an active key slot has no stripes or iterations.");
      var length = checked(this.KeyBytes * (int)slot.Stripes);
      var material = readKeyMaterial(slot.KeyMaterialOffset * (long)SectorSize, (length + SectorSize - 1) / SectorSize * SectorSize);
      var slotKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, slot.Salt, checked((int)slot.Iterations), hash, this.KeyBytes);
      using (var cipher = this.CreateCipher(slotKey))
        cipher.Decrypt(0, material);
      var masterKey = AntiForensicSplitter.Merge(material.AsSpan(0, length), this.KeyBytes, (int)slot.Stripes, hash);
      var check = Rfc2898DeriveBytes.Pbkdf2(masterKey, this.DigestSalt, checked((int)this.DigestIterations), hash, DigestSize);
      if (CryptographicOperations.FixedTimeEquals(check, this.Digest))
        return masterKey;
    }
    throw new CryptographicException("LUKS: the passphrase opens none of the key slots.");
  }

  /// <summary>The payload cipher of this header keyed with <paramref name="masterKey"/>.</summary>
  public Luks1SectorCipher CreateCipher(byte[] masterKey) => Luks1SectorCipher.Create(this.CipherName, this.CipherMode, masterKey);

  /// <summary>
  /// Builds a LUKS1 header with key slot 0 holding <paramref name="masterKey"/> under
  /// <paramref name="passphrase"/>: <c>aes</c> in <paramref name="cipherMode"/>, 4000 stripes per slot,
  /// each slot's material on a 4096-byte boundary, the payload after the last slot.
  /// </summary>
  /// <returns>The header followed by all key material, <see cref="PayloadOffset"/> sectors long.</returns>
  public static byte[] Create(byte[] masterKey, byte[] passphrase, string cipherMode, string hashSpec,
      int slotIterations, int digestIterations, Guid uuid) {
    ArgumentNullException.ThrowIfNull(masterKey);
    ArgumentNullException.ThrowIfNull(passphrase);
    const int stripes = 4000, alignSectors = 8;
    var hash = HashFor(hashSpec);
    var keyBytes = masterKey.Length;
    var slotSectors = (keyBytes * stripes + SectorSize - 1) / SectorSize;
    slotSectors = (slotSectors + alignSectors - 1) / alignSectors * alignSectors;
    var firstSlot = (HeaderSize + SectorSize - 1) / SectorSize;
    firstSlot = (firstSlot + alignSectors - 1) / alignSectors * alignSectors;
    var payload = firstSlot + SlotCount * slotSectors;

    var image = new byte[payload * SectorSize];
    var h = image.AsSpan();
    Magic.CopyTo(h);
    BinaryPrimitives.WriteUInt16BigEndian(h[6..], 1);
    WriteText(h.Slice(8, 32), "aes");
    WriteText(h.Slice(40, 32), cipherMode);
    WriteText(h.Slice(72, 32), hashSpec);
    BinaryPrimitives.WriteUInt32BigEndian(h[104..], (uint)payload);
    BinaryPrimitives.WriteUInt32BigEndian(h[108..], (uint)keyBytes);
    var digestSalt = RandomNumberGenerator.GetBytes(SaltSize);
    Rfc2898DeriveBytes.Pbkdf2(masterKey, digestSalt, digestIterations, hash, DigestSize).CopyTo(h[112..]);
    digestSalt.CopyTo(h[132..]);
    BinaryPrimitives.WriteUInt32BigEndian(h[164..], (uint)digestIterations);
    WriteText(h.Slice(168, 40), uuid.ToString("D"));

    for (var i = 0; i < SlotCount; ++i) {
      var s = h.Slice(208 + 48 * i, 48);
      var offset = firstSlot + i * slotSectors;
      BinaryPrimitives.WriteUInt32BigEndian(s[40..], (uint)offset);
      BinaryPrimitives.WriteUInt32BigEndian(s[44..], stripes);
      if (i != 0) {
        BinaryPrimitives.WriteUInt32BigEndian(s, SlotDisabled);
        continue;
      }
      var salt = RandomNumberGenerator.GetBytes(SaltSize);
      BinaryPrimitives.WriteUInt32BigEndian(s, SlotActive);
      BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)slotIterations);
      salt.CopyTo(s[8..]);
      var material = new byte[slotSectors * SectorSize];
      AntiForensicSplitter.Split(masterKey, stripes, hash).CopyTo(material, 0);
      var slotKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, slotIterations, hash, keyBytes);
      using (var cipher = Luks1SectorCipher.Create("aes", cipherMode, slotKey))
        cipher.Encrypt(0, material);
      material.CopyTo(image, offset * SectorSize);
    }
    return image;
  }

  /// <summary>The PBKDF2/splitter hash a LUKS hash spec names.</summary>
  /// <exception cref="NotSupportedException">The hash is not one of sha1, sha256, sha384, sha512.</exception>
  public static HashAlgorithmName HashFor(string spec) => spec.ToLowerInvariant() switch {
    "sha1" => HashAlgorithmName.SHA1,
    "sha256" => HashAlgorithmName.SHA256,
    "sha384" => HashAlgorithmName.SHA384,
    "sha512" => HashAlgorithmName.SHA512,
    _ => throw new NotSupportedException($"LUKS: hash '{spec}' is not supported."),
  };

  private static string Text(ReadOnlySpan<byte> field) {
    var end = field.IndexOf((byte)0);
    return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
  }

  private static void WriteText(Span<byte> field, string text) {
    var bytes = Encoding.ASCII.GetBytes(text);
    if (bytes.Length >= field.Length)
      throw new ArgumentException($"LUKS: '{text}' does not fit its header field.");
    bytes.CopyTo(field);
  }
}

/// <summary>One LUKS1 key slot.</summary>
/// <param name="Active">Whether the slot holds a key.</param>
/// <param name="Iterations">PBKDF2 iterations of the passphrase.</param>
/// <param name="Salt">PBKDF2 salt.</param>
/// <param name="KeyMaterialOffset">Start of the split key material, in sectors from the header.</param>
/// <param name="Stripes">Anti-forensic stripes.</param>
public sealed record Luks1KeySlot(bool Active, uint Iterations, byte[] Salt, uint KeyMaterialOffset, uint Stripes);

/// <summary>The LUKS anti-forensic information splitter (AF-split / AF-merge with hash diffusion).</summary>
public static class AntiForensicSplitter {
  /// <summary>Recovers the key from <paramref name="stripes"/> stripes of <paramref name="keyBytes"/> bytes.</summary>
  public static byte[] Merge(ReadOnlySpan<byte> material, int keyBytes, int stripes, HashAlgorithmName hash) {
    var d = new byte[keyBytes];
    for (var i = 0; i < stripes - 1; ++i) {
      Xor(d, material.Slice(i * keyBytes, keyBytes));
      Diffuse(d, hash);
    }
    Xor(d, material.Slice((stripes - 1) * keyBytes, keyBytes));
    return d;
  }

  /// <summary>Splits <paramref name="key"/> into <paramref name="stripes"/> stripes, all but the last random.</summary>
  public static byte[] Split(byte[] key, int stripes, HashAlgorithmName hash) {
    var n = key.Length;
    var material = new byte[n * stripes];
    RandomNumberGenerator.Fill(material.AsSpan(0, n * (stripes - 1)));
    var d = new byte[n];
    for (var i = 0; i < stripes - 1; ++i) {
      Xor(d, material.AsSpan(i * n, n));
      Diffuse(d, hash);
    }
    var last = material.AsSpan((stripes - 1) * n, n);
    key.CopyTo(last);
    Xor(last, d);
    return material;
  }

  private static void Xor(Span<byte> target, ReadOnlySpan<byte> source) {
    for (var i = 0; i < target.Length; ++i)
      target[i] ^= source[i];
  }

  /// <summary>Each digest-sized block becomes H(be32(block index) || block), cut to the block's length.</summary>
  private static void Diffuse(Span<byte> d, HashAlgorithmName hash) {
    using var h = IncrementalHash.CreateHash(hash);
    var size = h.HashLengthInBytes;
    Span<byte> index = stackalloc byte[4];
    Span<byte> digest = stackalloc byte[size];
    for (int block = 0, at = 0; at < d.Length; ++block, at += size) {
      var length = Math.Min(size, d.Length - at);
      BinaryPrimitives.WriteUInt32BigEndian(index, (uint)block);
      h.AppendData(index);
      h.AppendData(d.Slice(at, length));
      h.GetHashAndReset(digest);
      digest[..length].CopyTo(d.Slice(at, length));
    }
  }
}

/// <summary>
/// A sector cipher of the dm-crypt family: AES in XTS, CBC or ECB mode, 512-byte sectors, with the
/// <c>plain</c> (32-bit sector number), <c>plain64</c> (64-bit) or <c>essiv:&lt;hash&gt;</c> IV.
/// </summary>
public sealed class Luks1SectorCipher : IDisposable {
  private const int SectorSize = 512;
  private enum Mode { Xts, Cbc, Ecb }
  private enum IvKind { None, Plain, Plain64, Essiv }

  private readonly Mode _mode;
  private readonly IvKind _iv;
  private readonly Aes _data;
  private readonly Aes? _tweak;
  private readonly Aes? _essiv;

  private Luks1SectorCipher(Mode mode, IvKind iv, Aes data, Aes? tweak, Aes? essiv) {
    this._mode = mode;
    this._iv = iv;
    this._data = data;
    this._tweak = tweak;
    this._essiv = essiv;
  }

  /// <summary>Builds the cipher a LUKS header names, keyed with <paramref name="key"/>.</summary>
  /// <exception cref="NotSupportedException">The cipher, mode or IV generator is not supported.</exception>
  public static Luks1SectorCipher Create(string cipherName, string cipherMode, byte[] key) {
    if (!string.Equals(cipherName, "aes", StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException($"LUKS: cipher '{cipherName}' is not supported (AES only).");
    var dash = cipherMode.IndexOf('-');
    var modeText = dash < 0 ? cipherMode : cipherMode[..dash];
    var ivText = dash < 0 ? "" : cipherMode[(dash + 1)..];
    var mode = modeText.ToLowerInvariant() switch {
      "xts" => Mode.Xts,
      "cbc" => Mode.Cbc,
      "ecb" => Mode.Ecb,
      _ => throw new NotSupportedException($"LUKS: cipher mode '{cipherMode}' is not supported."),
    };
    var iv = ivText.ToLowerInvariant() switch {
      "" when mode == Mode.Ecb => IvKind.None,
      "plain" => IvKind.Plain,
      "plain64" => IvKind.Plain64,
      _ when ivText.StartsWith("essiv:", StringComparison.OrdinalIgnoreCase) => IvKind.Essiv,
      _ => throw new NotSupportedException($"LUKS: IV generator '{ivText}' of '{cipherMode}' is not supported."),
    };
    Aes? essiv = null;
    if (iv == IvKind.Essiv) {
      var essivKey = CryptographicOperations.HashData(Luks1Header.HashFor(ivText[6..]), key);
      if (essivKey.Length is not (16 or 24 or 32))
        throw new NotSupportedException($"LUKS: ESSIV hash '{ivText[6..]}' gives no valid AES key length.");
      essiv = NewAes(essivKey);
    }
    if (mode == Mode.Xts) {
      if (key.Length is not (32 or 48 or 64))
        throw new NotSupportedException($"LUKS: {key.Length}-byte XTS keys are not supported.");
      var half = key.Length / 2;
      return new Luks1SectorCipher(mode, iv, NewAes(key[..half]), NewAes(key[half..]), essiv);
    }
    if (key.Length is not (16 or 24 or 32))
      throw new NotSupportedException($"LUKS: {key.Length}-byte AES keys are not supported.");
    return new Luks1SectorCipher(mode, iv, NewAes(key), null, essiv);
  }

  private static Aes NewAes(byte[] key) {
    var aes = Aes.Create();
    aes.Key = key;
    return aes;
  }

  /// <summary>Decrypts whole sectors in place, the first numbered <paramref name="firstSector"/>.</summary>
  public void Decrypt(ulong firstSector, Span<byte> data) => this.Transform(firstSector, data, encrypt: false);

  /// <summary>Encrypts whole sectors in place, the first numbered <paramref name="firstSector"/>.</summary>
  public void Encrypt(ulong firstSector, Span<byte> data) => this.Transform(firstSector, data, encrypt: true);

  private void Transform(ulong firstSector, Span<byte> data, bool encrypt) {
    if (data.Length % SectorSize != 0)
      throw new ArgumentException("Sector ciphers work on whole 512-byte sectors.", nameof(data));
    Span<byte> iv = stackalloc byte[16];
    Span<byte> buffer = stackalloc byte[SectorSize];
    for (var at = 0; at < data.Length; at += SectorSize) {
      var sector = data.Slice(at, SectorSize);
      this.MakeIv(firstSector + (ulong)(at / SectorSize), iv);
      switch (this._mode) {
        case Mode.Ecb:
          if (encrypt) this._data.EncryptEcb(sector, buffer, PaddingMode.None);
          else this._data.DecryptEcb(sector, buffer, PaddingMode.None);
          break;
        case Mode.Cbc:
          if (encrypt) this._data.EncryptCbc(sector, iv, buffer, PaddingMode.None);
          else this._data.DecryptCbc(sector, iv, buffer, PaddingMode.None);
          break;
        default:
          this.Xts(sector, iv, buffer, encrypt);
          break;
      }
      buffer.CopyTo(sector);
    }
  }

  private void MakeIv(ulong sector, Span<byte> iv) {
    iv.Clear();
    switch (this._iv) {
      case IvKind.Plain:
        BinaryPrimitives.WriteUInt32LittleEndian(iv, (uint)sector);
        break;
      case IvKind.Plain64:
        BinaryPrimitives.WriteUInt64LittleEndian(iv, sector);
        break;
      case IvKind.Essiv:
        BinaryPrimitives.WriteUInt64LittleEndian(iv, sector);
        this._essiv!.EncryptEcb(iv, iv, PaddingMode.None);
        break;
    }
  }

  /// <summary>IEEE 1619 XTS over one sector (a whole number of blocks, so no ciphertext stealing).</summary>
  private void Xts(ReadOnlySpan<byte> input, ReadOnlySpan<byte> iv, Span<byte> output, bool encrypt) {
    Span<byte> tweak = stackalloc byte[16];
    this._tweak!.EncryptEcb(iv, tweak, PaddingMode.None);
    var blocks = new byte[input.Length];
    for (var at = 0; at < input.Length; at += 16) {
      for (var i = 0; i < 16; ++i)
        blocks[at + i] = (byte)(input[at + i] ^ tweak[i]);
      var carry = 0;
      for (var i = 0; i < 16; ++i) {
        var next = tweak[i] >> 7;
        tweak[i] = (byte)((tweak[i] << 1) | carry);
        carry = next;
      }
      if (carry != 0)
        tweak[0] ^= 0x87;
    }
    if (encrypt) this._data.EncryptEcb(blocks, output, PaddingMode.None);
    else this._data.DecryptEcb(blocks, output, PaddingMode.None);
    this._tweak.EncryptEcb(iv, tweak, PaddingMode.None);
    for (var at = 0; at < input.Length; at += 16) {
      for (var i = 0; i < 16; ++i)
        output[at + i] ^= tweak[i];
      var carry = 0;
      for (var i = 0; i < 16; ++i) {
        var next = tweak[i] >> 7;
        tweak[i] = (byte)((tweak[i] << 1) | carry);
        carry = next;
      }
      if (carry != 0)
        tweak[0] ^= 0x87;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    this._data.Dispose();
    this._tweak?.Dispose();
    this._essiv?.Dispose();
  }
}
