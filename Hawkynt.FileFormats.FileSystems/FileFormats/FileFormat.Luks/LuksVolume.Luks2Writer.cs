using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Compression.Core.Crypto;

namespace FileFormat.Luks;

/// <summary>The key-derivation function protecting a newly written LUKS2 key slot.</summary>
/// <param name="Kind"><c>pbkdf2</c>, <c>argon2i</c> or <c>argon2id</c>.</param>
/// <param name="Cost">PBKDF2 iterations, or the Argon2 time cost.</param>
/// <param name="MemoryKiB">Argon2 memory cost in KiB; ignored for PBKDF2.</param>
/// <param name="Lanes">Argon2 parallelism; ignored for PBKDF2.</param>
public sealed record LuksKdf(string Kind, int Cost, int MemoryKiB = 0, int Lanes = 0) {
  /// <summary>PBKDF2-SHA256 at <paramref name="iterations"/>.</summary>
  public static LuksKdf Pbkdf2(int iterations = 100_000) => new("pbkdf2", iterations);
  /// <summary>Argon2id, cryptsetup's LUKS2 default.</summary>
  public static LuksKdf Argon2id(int time = 4, int memoryKiB = 1 << 20, int lanes = 4) => new("argon2id", time, memoryKiB, lanes);
}

public sealed partial class LuksVolume {
  private const int Luks2HeaderSize = 16384;          // binary header + 12 KiB JSON area, cryptsetup's default
  private const long Luks2KeyslotsOffset = 2 * Luks2HeaderSize;
  private const long Luks2DataOffset = 16L << 20;     // cryptsetup's default data offset

  /// <summary>
  /// Writes a LUKS2 volume as <c>cryptsetup luksFormat --type luks2</c> lays one out: two binary
  /// headers with their JSON areas (16 KiB each, SHA-256 checksummed), one aes-xts-plain64 key
  /// slot with a 512-bit volume key and 4000 AF stripes at 32 KiB, a PBKDF2-SHA256 volume-key
  /// digest, and the data segment at 16 MiB with dynamic size and 512-byte sectors.
  /// </summary>
  public static void WriteLuks2(Stream output, ReadOnlySpan<byte> payload, string passphrase, LuksKdf kdf,
      string? label = null, string? uuid = null, int digestIterations = 100_000) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(passphrase);
    ArgumentNullException.ThrowIfNull(kdf);
    if (payload.Length % SectorSize != 0)
      throw new ArgumentException("LUKS payloads must be whole 512-byte sectors.", nameof(payload));
    if (digestIterations < 1000)
      throw new ArgumentOutOfRangeException(nameof(digestIterations), "cryptsetup refuses fewer than 1000 iterations.");
    switch (kdf.Kind) {
      case "pbkdf2" when kdf.Cost < 1000:
        throw new ArgumentOutOfRangeException(nameof(kdf), "cryptsetup refuses fewer than 1000 PBKDF2 iterations.");
      case "argon2i" or "argon2id" when kdf.Cost < 1 || kdf.Lanes < 1 || kdf.MemoryKiB < 8 * kdf.Lanes:
        throw new ArgumentOutOfRangeException(nameof(kdf), "Argon2 needs a time cost of at least 1 and at least 8 KiB of memory per lane.");
      case "pbkdf2" or "argon2i" or "argon2id":
        break;
      default:
        throw new NotSupportedException($"LUKS2 key slots with the '{kdf.Kind}' KDF are not supported (pbkdf2, argon2i and argon2id are).");
    }
    if (label is not null && Encoding.UTF8.GetByteCount(label) > 47)
      throw new ArgumentException("A LUKS2 label holds at most 47 bytes.", nameof(label));

    const int keyBytes = 64, stripes = 4000;
    const string encryption = "aes-xts-plain64";
    var hash = HashAlgorithmName.SHA256;
    var volumeKey = RandomNumberGenerator.GetBytes(keyBytes);
    var password = Encoding.UTF8.GetBytes(passphrase);
    uuid ??= Guid.NewGuid().ToString();

    // Key slot: the AF-split volume key, encrypted under the passphrase-derived key.
    var slotSalt = RandomNumberGenerator.GetBytes(32);
    var slotKey = kdf.Kind switch {
      "argon2i" => Argon2.Hash(Argon2.Variant.Argon2i, password, slotSalt, kdf.Cost, kdf.MemoryKiB, kdf.Lanes, keyBytes),
      "argon2id" => Argon2.Hash(Argon2.Variant.Argon2id, password, slotSalt, kdf.Cost, kdf.MemoryKiB, kdf.Lanes, keyBytes),
      _ => Rfc2898DeriveBytes.Pbkdf2(password, slotSalt, kdf.Cost, hash, keyBytes),
    };
    var split = LuksAntiForensic.Split(volumeKey, stripes, hash);
    var areaSize = (split.Length + 4095) / 4096 * 4096;
    var material = new byte[areaSize];
    split.CopyTo(material, 0);
    using (var c = new LuksCipher("aes", "xts-plain64", slotKey))
      c.Encrypt(material, 0);
    CryptographicOperations.ZeroMemory(slotKey);

    var digestSalt = RandomNumberGenerator.GetBytes(32);
    var digest = Rfc2898DeriveBytes.Pbkdf2(volumeKey, digestSalt, digestIterations, hash, 32);
    var json = Luks2Json(kdf, slotSalt, areaSize, digestSalt, digest, digestIterations, keyBytes, stripes, encryption);
    if (json.Length >= Luks2HeaderSize - 4096)
      throw new InvalidOperationException("LUKS2: the JSON metadata does not fit its area.");

    var head = new byte[Luks2DataOffset];
    WriteLuks2Header(head.AsSpan(0, Luks2HeaderSize), primary: true, label, uuid, json, RandomNumberGenerator.GetBytes(64));
    WriteLuks2Header(head.AsSpan(Luks2HeaderSize, Luks2HeaderSize), primary: false, label, uuid, json, RandomNumberGenerator.GetBytes(64));
    material.CopyTo(head, Luks2KeyslotsOffset);
    output.Write(head);

    using var dataCipher = new LuksCipher("aes", "xts-plain64", volumeKey);
    CryptographicOperations.ZeroMemory(volumeKey);
    var buffer = new byte[1 << 20];
    for (var done = 0; done < payload.Length;) {
      var n = Math.Min(buffer.Length, payload.Length - done);
      payload.Slice(done, n).CopyTo(buffer);
      dataCipher.Encrypt(buffer.AsSpan(0, n), (ulong)(done / SectorSize));
      output.Write(buffer, 0, n);
      done += n;
    }
    CryptographicOperations.ZeroMemory(buffer);
  }

  private static byte[] Luks2Json(LuksKdf kdf, byte[] slotSalt, int areaSize, byte[] digestSalt, byte[] digest,
      int digestIterations, int keyBytes, int stripes, string encryption) {
    using var ms = new MemoryStream();
    using (var w = new Utf8JsonWriter(ms)) {
      w.WriteStartObject();
      w.WriteStartObject("keyslots");
      w.WriteStartObject("0");
      w.WriteString("type", "luks2");
      w.WriteNumber("key_size", keyBytes);
      w.WriteStartObject("af");
      w.WriteString("type", "luks1");
      w.WriteNumber("stripes", stripes);
      w.WriteString("hash", "sha256");
      w.WriteEndObject();
      w.WriteStartObject("area");
      w.WriteString("type", "raw");
      w.WriteString("offset", Luks2KeyslotsOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
      w.WriteString("size", areaSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
      w.WriteString("encryption", encryption);
      w.WriteNumber("key_size", keyBytes);
      w.WriteEndObject();
      w.WriteStartObject("kdf");
      w.WriteString("type", kdf.Kind);
      if (kdf.Kind == "pbkdf2") {
        w.WriteString("hash", "sha256");
        w.WriteNumber("iterations", kdf.Cost);
      } else {
        w.WriteNumber("time", kdf.Cost);
        w.WriteNumber("memory", kdf.MemoryKiB);
        w.WriteNumber("cpus", kdf.Lanes);
      }
      w.WriteString("salt", Convert.ToBase64String(slotSalt));
      w.WriteEndObject();
      w.WriteEndObject();
      w.WriteEndObject();

      w.WriteStartObject("tokens");
      w.WriteEndObject();

      w.WriteStartObject("segments");
      w.WriteStartObject("0");
      w.WriteString("type", "crypt");
      w.WriteString("offset", Luks2DataOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
      w.WriteString("size", "dynamic");
      w.WriteString("iv_tweak", "0");
      w.WriteString("encryption", encryption);
      w.WriteNumber("sector_size", SectorSize);
      w.WriteEndObject();
      w.WriteEndObject();

      w.WriteStartObject("digests");
      w.WriteStartObject("0");
      w.WriteString("type", "pbkdf2");
      w.WriteStartArray("keyslots");
      w.WriteStringValue("0");
      w.WriteEndArray();
      w.WriteStartArray("segments");
      w.WriteStringValue("0");
      w.WriteEndArray();
      w.WriteString("hash", "sha256");
      w.WriteNumber("iterations", digestIterations);
      w.WriteString("salt", Convert.ToBase64String(digestSalt));
      w.WriteString("digest", Convert.ToBase64String(digest));
      w.WriteEndObject();
      w.WriteEndObject();

      w.WriteStartObject("config");
      w.WriteString("json_size", (Luks2HeaderSize - 4096).ToString(System.Globalization.CultureInfo.InvariantCulture));
      w.WriteString("keyslots_size", (Luks2DataOffset - Luks2KeyslotsOffset).ToString(System.Globalization.CultureInfo.InvariantCulture));
      w.WriteEndObject();
      w.WriteEndObject();
    }
    return ms.ToArray();
  }

  /// <summary>One binary header plus its JSON area; the checksum covers both with the checksum field zeroed.</summary>
  private static void WriteLuks2Header(Span<byte> area, bool primary, string? label, string uuid, byte[] json, byte[] salt) {
    area.Clear();
    if (primary) Magic.CopyTo(area);
    else "SKUL"u8.CopyTo(area);
    area[4] = 0xBA;
    area[5] = 0xBE;
    BinaryPrimitives.WriteUInt16BigEndian(area[6..], 2);
    BinaryPrimitives.WriteUInt64BigEndian(area[8..], Luks2HeaderSize);
    BinaryPrimitives.WriteUInt64BigEndian(area[16..], 1);                    // seqid, equal in both copies
    if (label is not null) Encoding.UTF8.GetBytes(label).CopyTo(area[24..]);
    "sha256"u8.CopyTo(area[72..]);
    salt.CopyTo(area[104..]);
    Encoding.ASCII.GetBytes(uuid).CopyTo(area[168..]);
    BinaryPrimitives.WriteUInt64BigEndian(area[256..], primary ? 0UL : Luks2HeaderSize);
    json.CopyTo(area[4096..]);
    SHA256.HashData(area).CopyTo(area[448..]);
  }
}
