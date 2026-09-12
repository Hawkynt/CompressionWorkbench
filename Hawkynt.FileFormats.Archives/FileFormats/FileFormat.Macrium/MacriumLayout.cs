#pragma warning disable CS1591
using System.Text.Json;

namespace FileFormat.Macrium;

/// <summary>
/// Navigational layout fields parsed from a Reflect X <c>$JSON</c> metadata
/// block — the minimum surface area the sector-reconstruction path in
/// <see cref="MacriumReader"/> needs. We deliberately do not surface the full
/// JSON schema (the user can already read <c>metadata.json</c> for that).
///
/// <para>
/// JSON path summary per the vendor spec / examples:
/// </para>
/// <list type="bullet">
///   <item><description><c>_compression.compression_method</c> — "zstd" or "none".</description></item>
///   <item><description><c>_encryption.enable</c> — true when password protected.</description></item>
///   <item><description><c>_encryption.aes_type</c> — "aes-128" / "aes-192" / "aes-256".</description></item>
///   <item><description><c>_encryption.key_iterations</c> — PBKDF2 iterations.</description></item>
///   <item><description><c>_encryption.hmac</c> — hex string; <c>HMAC-SHA256(derived_key, "")</c>.</description></item>
///   <item><description><c>_header.imageid</c> — 16-hex-char string for the 8 raw imageid bytes.</description></item>
///   <item><description><c>disks[0]._header.disk_number</c></description></item>
///   <item><description><c>disks[0].partitions[0]._header.partition_number</c></description></item>
///   <item><description><c>disks[0].partitions[0]._header.block_size</c></description></item>
///   <item><description><c>disks[0].partitions[0]._cw_extra.partition_byte_size</c> — our own extension preserving the unpadded byte count for exact round-trip.</description></item>
/// </list>
/// </summary>
internal sealed class MacriumLayout {
  public byte[] ImageId { get; init; } = new byte[MacriumCrypto.ImageIdSize];
  public int DiskNumber { get; init; }
  public int PartitionNumber { get; init; }
  public int BlockSize { get; init; } = MacriumWriter.DefaultBlockSize;
  public int PartitionByteSize { get; init; }
  public int ReservedSectorsLength { get; init; }
  public bool IsZstd { get; init; }
  public bool IsEncrypted { get; init; }
  public MacriumAesType AesType { get; init; } = MacriumAesType.Aes256;
  public int KeyIterations { get; init; } = MacriumCrypto.DefaultPbkdf2Iterations;
  public byte[]? ExpectedHmac { get; init; }

  /// <summary>Absolute file offset where the disk-level metadata chain ($TRACK0/$EPT/$BITMAP/$INDEX) begins. 0 when the JSON doesn't surface it.</summary>
  public long IndexFilePosition { get; init; }

  public static MacriumLayout Parse(ReadOnlySpan<byte> jsonBytes) {
    using var doc = JsonDocument.Parse(jsonBytes.ToArray());
    var root = doc.RootElement;

    var imageIdHex = TryGetString(root, "_header", "imageid") ?? "";
    byte[] imageId;
    try { imageId = imageIdHex.Length == 16 ? MacriumCrypto.HexToBytes(imageIdHex) : new byte[MacriumCrypto.ImageIdSize]; }
    catch { imageId = new byte[MacriumCrypto.ImageIdSize]; }

    var compressionMethod = TryGetString(root, "_compression", "compression_method") ?? "";
    var isZstd = compressionMethod.Equals("zstd", StringComparison.OrdinalIgnoreCase);

    var encryptEnable = TryGetBool(root, "_encryption", "enable") ?? false;
    var aesTypeStr = TryGetString(root, "_encryption", "aes_type") ?? "aes-256";
    var aesType = aesTypeStr.ToLowerInvariant() switch {
      "aes-128" => MacriumAesType.Aes128,
      "aes-192" => MacriumAesType.Aes192,
      _ => MacriumAesType.Aes256,
    };
    var keyIter = TryGetInt(root, "_encryption", "key_iterations") ?? MacriumCrypto.DefaultPbkdf2Iterations;
    var hmacHex = TryGetString(root, "_encryption", "hmac");
    byte[]? expectedHmac = null;
    if (encryptEnable && !string.IsNullOrEmpty(hmacHex)) {
      try { expectedHmac = MacriumCrypto.HexToBytes(hmacHex); } catch { /* leave null */ }
    }

    var diskNumber = 0;
    var partitionNumber = 1;
    var blockSize = MacriumWriter.DefaultBlockSize;
    var partitionByteSize = 0;
    var reservedLen = 0;
    long indexFilePosition = 0;
    if (root.TryGetProperty("_header", out var hdr) && hdr.ValueKind == JsonValueKind.Object
        && hdr.TryGetProperty("index_file_position", out var ifp)
        && ifp.ValueKind == JsonValueKind.Number
        && ifp.TryGetInt64(out var ifpVal)) {
      indexFilePosition = ifpVal;
    }

    if (root.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Array && disks.GetArrayLength() > 0) {
      var disk0 = disks[0];
      diskNumber = TryGetInt(disk0, "_header", "disk_number") ?? 0;
      if (disk0.TryGetProperty("partitions", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0) {
        var p0 = parts[0];
        partitionNumber = TryGetInt(p0, "_header", "partition_number") ?? 1;
        blockSize = TryGetInt(p0, "_header", "block_size") ?? MacriumWriter.DefaultBlockSize;
        partitionByteSize = TryGetInt(p0, "_cw_extra", "partition_byte_size") ?? 0;
        reservedLen = TryGetInt(p0, "_cw_extra", "reserved_sectors_length") ?? 0;
      }
    }

    return new MacriumLayout {
      ImageId = imageId,
      DiskNumber = diskNumber,
      PartitionNumber = partitionNumber,
      BlockSize = blockSize,
      PartitionByteSize = partitionByteSize,
      ReservedSectorsLength = reservedLen,
      IsZstd = isZstd,
      IsEncrypted = encryptEnable,
      AesType = aesType,
      KeyIterations = keyIter,
      ExpectedHmac = expectedHmac,
      IndexFilePosition = indexFilePosition,
    };
  }

  private static string? TryGetString(JsonElement parent, string a, string b) {
    if (!parent.TryGetProperty(a, out var lhs) || lhs.ValueKind != JsonValueKind.Object) return null;
    return lhs.TryGetProperty(b, out var rhs) && rhs.ValueKind == JsonValueKind.String ? rhs.GetString() : null;
  }

  private static bool? TryGetBool(JsonElement parent, string a, string b) {
    if (!parent.TryGetProperty(a, out var lhs) || lhs.ValueKind != JsonValueKind.Object) return null;
    if (!lhs.TryGetProperty(b, out var rhs)) return null;
    return rhs.ValueKind switch {
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => null,
    };
  }

  private static int? TryGetInt(JsonElement parent, string a, string b) {
    if (!parent.TryGetProperty(a, out var lhs) || lhs.ValueKind != JsonValueKind.Object) return null;
    if (!lhs.TryGetProperty(b, out var rhs) || rhs.ValueKind != JsonValueKind.Number) return null;
    return rhs.TryGetInt32(out var n) ? n : (int?)null;
  }
}
