using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileFormat.Luks;

/// <summary>A key slot: where its split key lives and how to derive the key that unlocks it.</summary>
public sealed record LuksKeySlot(
  int Index, bool Active, string Kdf, string KdfHash, int Iterations, byte[] Salt,
  long MaterialOffset, long MaterialSize, int Stripes, string AfHash, string Cipher, string CipherMode, int KeyBytes) {
  /// <summary>Argon2 memory cost in KiB (LUKS2 <c>memory</c>); 0 for PBKDF2 slots.</summary>
  public int MemoryKiB { get; init; }
  /// <summary>Argon2 lanes (LUKS2 <c>cpus</c>); 0 for PBKDF2 slots.</summary>
  public int Lanes { get; init; }
}

/// <summary>
/// A LUKS1 or LUKS2 volume header and the operations that need the passphrase: unlocking a key
/// slot to recover the volume key and decrypting the data segment. Implemented from the LUKS1
/// On-Disk Format Specification 1.2.3 and the LUKS2 On-Disk Format Specification (cryptsetup);
/// see <c>docs/LUKS-ON-DISK.md</c>.
/// </summary>
public sealed class LuksVolume {
  public static ReadOnlySpan<byte> Magic => [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xBA, 0xBE];
  public const int SectorSize = 512;

  public int Version { get; private init; }
  public string Cipher { get; private init; } = "";
  public string CipherMode { get; private init; } = "";
  public string Hash { get; private init; } = "";
  public int KeyBytes { get; private init; }
  public string Uuid { get; private init; } = "";
  public string Label { get; private init; } = "";
  public long PayloadOffset { get; private init; }
  /// <summary>Payload length in bytes, or −1 for "to the end of the device".</summary>
  public long PayloadSize { get; private init; }
  public int PayloadSectorSize { get; private init; } = SectorSize;
  public ulong IvTweak { get; private init; }
  public IReadOnlyList<LuksKeySlot> KeySlots { get; private init; } = [];
  private byte[] DigestSalt { get; init; } = [];
  private byte[] Digest { get; init; } = [];
  private int DigestIterations { get; init; }
  private string DigestHash { get; init; } = "";

  public long PayloadLength(long deviceLength) => this.PayloadSize >= 0 ? this.PayloadSize : Math.Max(0, deviceLength - this.PayloadOffset);

  /// <exception cref="InvalidDataException">Not LUKS or a corrupt header.</exception>
  public static LuksVolume Read(Stream stream) {
    stream.Position = 0;
    var h = new byte[592];
    if (stream.ReadAtLeast(h, h.Length, throwOnEndOfStream: false) != h.Length || !h.AsSpan(0, 6).SequenceEqual(Magic))
      throw new InvalidDataException("LUKS: magic \"LUKS\\xBA\\xBE\" missing.");
    return BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(6)) switch {
      1 => ReadV1(h),
      2 => ReadV2(stream),
      var v => throw new NotSupportedException($"LUKS version {v} is not supported."),
    };
  }

  private static string Text(ReadOnlySpan<byte> field) => Encoding.ASCII.GetString(field[..(field.IndexOf((byte)0) is >= 0 and var n ? n : field.Length)]);

  private static LuksVolume ReadV1(byte[] h) {
    var keyBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(108));
    var cipher = Text(h.AsSpan(8, 32));
    var mode = Text(h.AsSpan(40, 32));
    var hash = Text(h.AsSpan(72, 32));
    var slots = new List<LuksKeySlot>();
    for (var i = 0; i < 8; ++i) {
      var s = h.AsSpan(208 + 48 * i, 48);
      var state = BinaryPrimitives.ReadUInt32BigEndian(s);
      if (state is not (0x00AC71F3 or 0x0000DEAD))
        throw new InvalidDataException($"LUKS: key slot {i} has an invalid state 0x{state:X8}.");
      var stripes = (int)BinaryPrimitives.ReadUInt32BigEndian(s[44..]);
      slots.Add(new(i, state == 0x00AC71F3, "pbkdf2", hash, (int)BinaryPrimitives.ReadUInt32BigEndian(s[4..]), s.Slice(8, 32).ToArray(),
        BinaryPrimitives.ReadUInt32BigEndian(s[40..]) * (long)SectorSize, (long)keyBytes * stripes, stripes, hash, cipher, mode, keyBytes));
    }
    if (keyBytes is < 16 or > 64)
      throw new InvalidDataException($"LUKS: implausible key size {keyBytes}.");
    return new LuksVolume {
      Version = 1, Cipher = cipher, CipherMode = mode, Hash = hash, KeyBytes = keyBytes,
      PayloadOffset = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(104)) * (long)SectorSize, PayloadSize = -1,
      Digest = h.AsSpan(112, 20).ToArray(), DigestSalt = h.AsSpan(132, 32).ToArray(),
      DigestIterations = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(164)), DigestHash = hash,
      Uuid = Text(h.AsSpan(168, 40)), KeySlots = slots,
    };
  }

  private static LuksVolume ReadV2(Stream stream) {
    stream.Position = 0;
    var bin = new byte[4096];
    stream.ReadExactly(bin);
    var hdrSize = BinaryPrimitives.ReadInt64BigEndian(bin.AsSpan(8));
    if (hdrSize is < 16384 or > 4 << 20 || hdrSize % 4096 != 0)
      throw new InvalidDataException($"LUKS2: implausible header size {hdrSize}.");
    var area = new byte[hdrSize];
    stream.Position = 0;
    if (stream.ReadAtLeast(area, area.Length, throwOnEndOfStream: false) != area.Length)
      throw new InvalidDataException("LUKS2: header area truncated.");
    var csumAlg = Text(bin.AsSpan(72, 32));
    var stored = bin.AsSpan(448, 64).ToArray();
    area.AsSpan(448, 64).Clear();
    var computed = csumAlg.ToLowerInvariant() switch {
      "sha256" => SHA256.HashData(area),
      "sha512" => SHA512.HashData(area),
      "sha1" => SHA1.HashData(area),
      _ => throw new NotSupportedException($"LUKS2: header checksum '{csumAlg}' is not supported."),
    };
    if (!stored.AsSpan(0, computed.Length).SequenceEqual(computed))
      throw new InvalidDataException("LUKS2: header checksum mismatch.");

    var json = area.AsSpan(4096);
    var end = json.IndexOf((byte)0);
    using var doc = JsonDocument.Parse(json[..(end < 0 ? json.Length : end)].ToArray());
    var root = doc.RootElement;

    var segment = root.GetProperty("segments").EnumerateObject().OrderBy(p => long.Parse(p.Name)).First().Value;
    if (segment.GetProperty("type").GetString() != "crypt")
      throw new NotSupportedException("LUKS2: the first data segment is not a crypt segment (reencryption in progress?).");
    var (cipher, mode) = SplitCipher(segment.GetProperty("encryption").GetString()!);
    var sizeText = segment.GetProperty("size").GetString();
    var digest = root.GetProperty("digests").EnumerateObject().First().Value;
    if (digest.GetProperty("type").GetString() != "pbkdf2")
      throw new NotSupportedException("LUKS2: only pbkdf2 volume-key digests are defined.");

    var slots = new List<LuksKeySlot>();
    foreach (var p in root.GetProperty("keyslots").EnumerateObject()) {
      var k = p.Value;
      var kdf = k.GetProperty("kdf");
      var kdfType = kdf.GetProperty("type").GetString()!;
      var areaEl = k.GetProperty("area");
      var af = k.GetProperty("af");
      var (ac, am) = SplitCipher(areaEl.GetProperty("encryption").GetString()!);
      var keySize = k.GetProperty("key_size").GetInt32();
      var stripes = af.GetProperty("stripes").GetInt32();
      slots.Add(new(int.Parse(p.Name), k.GetProperty("type").GetString() == "luks2", kdfType,
        kdf.TryGetProperty("hash", out var kh) ? kh.GetString()! : "",
        kdf.TryGetProperty("iterations", out var it) ? it.GetInt32() : kdf.TryGetProperty("time", out var tm) ? tm.GetInt32() : 0,
        Convert.FromBase64String(kdf.GetProperty("salt").GetString()!),
        long.Parse(areaEl.GetProperty("offset").GetString()!), long.Parse(areaEl.GetProperty("size").GetString()!),
        stripes, af.GetProperty("hash").GetString()!, ac, am, areaEl.GetProperty("key_size").GetInt32()) {
        MemoryKiB = kdf.TryGetProperty("memory", out var mem) ? mem.GetInt32() : 0,
        Lanes = kdf.TryGetProperty("cpus", out var cpus) ? cpus.GetInt32() : 0,
      });
      if (keySize is < 16 or > 64) throw new InvalidDataException("LUKS2: implausible key size.");
    }

    return new LuksVolume {
      Version = 2, Cipher = cipher, CipherMode = mode, Hash = digest.GetProperty("hash").GetString()!,
      KeyBytes = root.GetProperty("keyslots").EnumerateObject().First().Value.GetProperty("key_size").GetInt32(),
      PayloadOffset = long.Parse(segment.GetProperty("offset").GetString()!),
      PayloadSize = sizeText == "dynamic" ? -1 : long.Parse(sizeText!),
      PayloadSectorSize = segment.GetProperty("sector_size").GetInt32(),
      IvTweak = ulong.Parse(segment.GetProperty("iv_tweak").GetString()!),
      Digest = Convert.FromBase64String(digest.GetProperty("digest").GetString()!),
      DigestSalt = Convert.FromBase64String(digest.GetProperty("salt").GetString()!),
      DigestIterations = digest.GetProperty("iterations").GetInt32(), DigestHash = digest.GetProperty("hash").GetString()!,
      Uuid = Text(bin.AsSpan(168, 40)), Label = Text(bin.AsSpan(24, 48)), KeySlots = slots,
    };
  }

  private static (string Cipher, string Mode) SplitCipher(string spec) {
    var dash = spec.IndexOf('-');
    return dash < 0 ? (spec, "") : (spec[..dash], spec[(dash + 1)..]);
  }

  /// <summary>Tries every active key slot; returns the verified volume key.</summary>
  /// <exception cref="InvalidDataException">No key slot opens with this passphrase.</exception>
  /// <exception cref="NotSupportedException">The only candidate slots use an unsupported KDF or cipher.</exception>
  public byte[] Unlock(Stream stream, string passphrase) {
    var password = Encoding.UTF8.GetBytes(passphrase);
    NotSupportedException? unsupported = null;
    foreach (var slot in this.KeySlots.Where(s => s.Active)) {
      if (slot.Kdf is not ("pbkdf2" or "argon2i" or "argon2id")) {
        unsupported ??= new NotSupportedException($"LUKS: key slot {slot.Index} uses the '{slot.Kdf}' KDF, which is not supported (pbkdf2, argon2i and argon2id are).");
        continue;
      }
      if (!LuksCipher.IsSupported(slot.Cipher, slot.CipherMode)) {
        unsupported ??= new NotSupportedException($"LUKS: key slot {slot.Index} uses '{slot.Cipher}-{slot.CipherMode}', which is not supported.");
        continue;
      }
      var slotKey = slot.Kdf switch {
        "argon2i" => Compression.Core.Crypto.Argon2.Hash(Compression.Core.Crypto.Argon2.Variant.Argon2i, password, slot.Salt, slot.Iterations, slot.MemoryKiB, slot.Lanes, slot.KeyBytes),
        "argon2id" => Compression.Core.Crypto.Argon2.Hash(Compression.Core.Crypto.Argon2.Variant.Argon2id, password, slot.Salt, slot.Iterations, slot.MemoryKiB, slot.Lanes, slot.KeyBytes),
        _ => Rfc2898DeriveBytes.Pbkdf2(password, slot.Salt, slot.Iterations, LuksAntiForensic.HashName(slot.KdfHash), slot.KeyBytes),
      };
      var materialLength = (int)(((long)this.KeyBytes * slot.Stripes + SectorSize - 1) / SectorSize * SectorSize);
      var material = new byte[materialLength];
      stream.Position = slot.MaterialOffset;
      if (stream.ReadAtLeast(material, material.Length, throwOnEndOfStream: false) != material.Length)
        throw new InvalidDataException($"LUKS: key slot {slot.Index} material runs past the end of the device.");
      using (var c = new LuksCipher(slot.Cipher, slot.CipherMode, slotKey))
        c.Decrypt(material, 0);
      var candidate = LuksAntiForensic.Merge(material, this.KeyBytes, slot.Stripes, LuksAntiForensic.HashName(slot.AfHash));
      if (this.VerifyKey(candidate))
        return candidate;
    }
    if (unsupported is not null && !this.KeySlots.Any(s => s.Active && s.Kdf is "pbkdf2" or "argon2i" or "argon2id"))
      throw unsupported;
    throw new InvalidDataException("LUKS: no key slot opens with this passphrase.");
  }

  public bool VerifyKey(ReadOnlySpan<byte> volumeKey) {
    var check = Rfc2898DeriveBytes.Pbkdf2(volumeKey, this.DigestSalt, this.DigestIterations, LuksAntiForensic.HashName(this.DigestHash), this.Digest.Length);
    return CryptographicOperations.FixedTimeEquals(check, this.Digest);
  }

  /// <summary>Decrypts the data segment into <paramref name="output"/>.</summary>
  public void DecryptPayload(Stream stream, byte[] volumeKey, Stream output) {
    var length = this.PayloadLength(stream.Length);
    if (length % this.PayloadSectorSize != 0)
      throw new InvalidDataException("LUKS: the data segment is not a whole number of sectors.");
    using var cipher = new LuksCipher(this.Cipher, this.CipherMode, volumeKey);
    var buffer = new byte[Math.Max(this.PayloadSectorSize, 1 << 20) / this.PayloadSectorSize * this.PayloadSectorSize];
    stream.Position = this.PayloadOffset;
    for (long done = 0; done < length;) {
      var n = (int)Math.Min(buffer.Length, length - done);
      stream.ReadExactly(buffer, 0, n);
      cipher.Decrypt(buffer.AsSpan(0, n), this.IvTweak + (ulong)(done / SectorSize), this.PayloadSectorSize);
      output.Write(buffer, 0, n);
      done += n;
    }
  }

  /// <summary>
  /// Writes a LUKS1 volume: aes-xts-plain64 with a 512-bit volume key, PBKDF2 over
  /// <paramref name="hash"/>, one active key slot, 4000 stripes, payload at 2 MiB.
  /// </summary>
  public static void WriteLuks1(Stream output, ReadOnlySpan<byte> payload, string passphrase, int iterations = 100_000,
      string hash = "sha256", string? uuid = null) {
    if (payload.Length % SectorSize != 0)
      throw new ArgumentException("LUKS payloads must be whole 512-byte sectors.", nameof(payload));
    if (iterations < 1000)
      throw new ArgumentOutOfRangeException(nameof(iterations), "cryptsetup refuses fewer than 1000 iterations.");
    const int keyBytes = 64, stripes = 4000, payloadSectors = 4096;
    const string cipher = "aes", mode = "xts-plain64";
    var hashName = LuksAntiForensic.HashName(hash);
    var volumeKey = RandomNumberGenerator.GetBytes(keyBytes);
    var password = Encoding.UTF8.GetBytes(passphrase);

    var h = new byte[592];
    Magic.CopyTo(h);
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(6), 1);
    Encoding.ASCII.GetBytes(cipher).CopyTo(h, 8);
    Encoding.ASCII.GetBytes(mode).CopyTo(h, 40);
    Encoding.ASCII.GetBytes(hash).CopyTo(h, 72);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(104), payloadSectors);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(108), keyBytes);
    var digestSalt = RandomNumberGenerator.GetBytes(32);
    Rfc2898DeriveBytes.Pbkdf2(volumeKey, digestSalt, iterations, hashName, 20).CopyTo(h, 112);
    digestSalt.CopyTo(h, 132);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(164), (uint)iterations);
    Encoding.ASCII.GetBytes(uuid ?? Guid.NewGuid().ToString()).CopyTo(h, 168);

    var materialSectors = (keyBytes * stripes + 4095) / 4096 * 8; // 4 KiB aligned, as cryptsetup lays slots out
    byte[]? material = null;
    for (var i = 0; i < 8; ++i) {
      var s = h.AsSpan(208 + 48 * i, 48);
      var offsetSectors = 8 + i * materialSectors;
      BinaryPrimitives.WriteUInt32BigEndian(s, i == 0 ? 0x00AC71F3u : 0x0000DEADu);
      BinaryPrimitives.WriteUInt32BigEndian(s[44..], stripes);
      BinaryPrimitives.WriteUInt32BigEndian(s[40..], (uint)offsetSectors);
      if (i != 0) continue;
      var salt = RandomNumberGenerator.GetBytes(32);
      salt.CopyTo(s[8..]);
      BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)iterations);
      var slotKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, hashName, keyBytes);
      var split = LuksAntiForensic.Split(volumeKey, stripes, hashName);
      material = new byte[(split.Length + SectorSize - 1) / SectorSize * SectorSize];
      split.CopyTo(material, 0);
      using var c = new LuksCipher(cipher, mode, slotKey);
      c.Encrypt(material, 0);
    }

    var head = new byte[payloadSectors * SectorSize];
    h.CopyTo(head, 0);
    material!.CopyTo(head, 8 * SectorSize);
    output.Write(head);

    using var dataCipher = new LuksCipher(cipher, mode, volumeKey);
    var buffer = new byte[1 << 20];
    for (var done = 0; done < payload.Length;) {
      var n = Math.Min(buffer.Length, payload.Length - done);
      payload.Slice(done, n).CopyTo(buffer);
      dataCipher.Encrypt(buffer.AsSpan(0, n), (ulong)(done / SectorSize));
      output.Write(buffer, 0, n);
      done += n;
    }
  }
}
