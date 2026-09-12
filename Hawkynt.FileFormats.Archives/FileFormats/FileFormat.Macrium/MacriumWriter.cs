#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Streams;
using FileFormat.Zstd;

namespace FileFormat.Macrium;

/// <summary>
/// Writes a valid Macrium Reflect X (<c>.mrimgx</c>) container from a flat
/// disk-image payload, following the MIT-licensed vendor spec at
/// <see href="https://github.com/macrium/mrimgx_file_layout"/>.
///
/// <para>
/// <b>Container layout produced:</b>
/// </para>
/// <list type="number">
///   <item><description><b>Reserved-sectors prefix</b> — first <see cref="ReservedSectorsLength"/> bytes
///     of the input disk image are emitted as a sequence of fixed-size data blocks (no compression /
///     encryption metadata applied per Macrium's "$TRACK0 always uncompressed" convention; we keep
///     it simple and emit them as plain data blocks).</description></item>
///   <item><description><b>Partition data blocks</b> — remaining bytes split into
///     <see cref="BlockSize"/>-byte data blocks, each independently zstd-compressed and / or
///     AES-CBC encrypted depending on the writer's mode. The last block is zero-padded to a full
///     <see cref="BlockSize"/> on disk so the reader can restore byte counts exactly via the JSON
///     <c>partition_byte_size</c> field.</description></item>
///   <item><description><b>Metadata chain</b> at <c>index_file_position</c> — written in this order
///     so the reader can walk it forward: <c>$TRACK0</c> (raw MBR/GPT prefix copy), <c>$INDEX</c>
///     (per-partition <c>DataBlockIndexElement[]</c>), <c>$JSON</c> (navigational metadata), and
///     <c>$AUXDATA</c> (terminal block with <c>last=1</c>).</description></item>
///   <item><description><b>20-byte footer</b> — <c>uint64 first_metadata_block_offset LE</c> +
///     ASCII <c>"MACRIUM_FILE"</c>.</description></item>
/// </list>
///
/// <para>
/// <b>What this writer does NOT emit</b> (intentional, callers don't need them
/// for round-trip):
/// </para>
/// <list type="bullet">
///   <item><description><c>$BITMAP</c> — only populated for exFAT/ReFS per spec, not required for restore.</description></item>
///   <item><description><c>$EPT</c> — extended partition table; only required for MBR disks with extended partitions.</description></item>
///   <item><description><c>$AUXDATA</c> root payload (we emit only the empty terminator).</description></item>
///   <item><description><c>Reserved Sectors Index</c> — only required for FAT12/16/32; out of scope.</description></item>
/// </list>
/// </summary>
public sealed class MacriumWriter {

  /// <summary>Default partition block size = 64 KB (matches Macrium's spec example).</summary>
  public const int DefaultBlockSize = 65536;

  /// <summary>Bytes copied verbatim into <c>$TRACK0</c>: MBR (sector 1) up to first partition start. Max 1 MB per spec.</summary>
  private const int Track0MaxLength = 1 * 1024 * 1024;

  /// <summary>The partition block size, in bytes. Always a multiple of 512.</summary>
  public int BlockSize { get; init; } = DefaultBlockSize;

  /// <summary>Optional zstd compression of data blocks (and metadata blocks). Default off for round-trip predictability.</summary>
  public bool CompressDataBlocks { get; init; }

  /// <summary>Optional AES-CBC encryption of data blocks. Default off.</summary>
  public bool EncryptDataBlocks { get; init; }

  /// <summary>Encryption password (required when <see cref="EncryptDataBlocks"/> is true).</summary>
  public string? Password { get; init; }

  /// <summary>AES variant for data block encryption. Honoured only when <see cref="EncryptDataBlocks"/> is true.</summary>
  public MacriumAesType AesType { get; init; } = MacriumAesType.Aes256;

  /// <summary>PBKDF2 iteration count for password key derivation. Spec default = 600 000.</summary>
  public int Pbkdf2Iterations { get; init; } = MacriumCrypto.DefaultPbkdf2Iterations;

  /// <summary>Image identifier (8 raw bytes => 16 hex chars in JSON). Random by default; explicit for round-trip tests.</summary>
  public byte[]? ImageId { get; init; }

  /// <summary>Disk number to advertise in <c>$JSON.disks[0]._header.disk_number</c>. Default = 0.</summary>
  public int DiskNumber { get; init; }

  /// <summary>Partition number to advertise in <c>$JSON.disks[0].partitions[0]._header.partition_number</c>. Default = 1.</summary>
  public int PartitionNumber { get; init; } = 1;

  /// <summary>Total bytes copied verbatim into <c>$TRACK0</c>. Capped at 1 MB per spec.</summary>
  public int ReservedSectorsLength { get; init; }

  /// <summary>
  /// Builds a Reflect X container for <paramref name="diskImage"/> in memory.
  /// </summary>
  /// <param name="diskImage">The raw disk image bytes to embed.</param>
  /// <returns>The complete <c>.mrimgx</c> file as a byte array.</returns>
  public byte[] Build(ReadOnlySpan<byte> diskImage) {
    if (this.BlockSize <= 0 || this.BlockSize % 512 != 0)
      throw new InvalidOperationException("BlockSize must be a positive multiple of 512.");
    if (this.EncryptDataBlocks && string.IsNullOrEmpty(this.Password))
      throw new InvalidOperationException("Password is required when EncryptDataBlocks is true.");

    var imageIdBytes = this.ImageId ?? GenerateImageId();
    if (imageIdBytes.Length != MacriumCrypto.ImageIdSize)
      throw new InvalidOperationException($"ImageId must be {MacriumCrypto.ImageIdSize} bytes.");

    // Derive crypto material once if needed.
    byte[]? derivedKey = null;
    byte[]? aesKey = null;
    byte[]? hmac = null;
    if (this.EncryptDataBlocks) {
      derivedKey = MacriumCrypto.DeriveKey(this.Password!, imageIdBytes, this.Pbkdf2Iterations);
      aesKey = derivedKey[..(int)this.AesType];
      hmac = MacriumCrypto.ComputeHmac(derivedKey);
    }

    using var ms = new MemoryStream();

    // ── 1) $TRACK0 raw prefix copied directly (no framing) — we keep the
    // partition starting at offset 0 of the input by reading the prefix from
    // the input and storing it later in the $TRACK0 metadata block. The
    // partition payload bytes are written starting at offset 0 of the file as
    // data blocks.
    var reservedLen = Math.Clamp(this.ReservedSectorsLength, 0, Track0MaxLength);
    if (reservedLen > diskImage.Length)
      reservedLen = diskImage.Length;
    var track0 = diskImage[..reservedLen].ToArray();
    var partitionPayload = diskImage[reservedLen..];

    // ── 2) Write partition data blocks ────────────────────────────────────
    var indexElements = new List<DataBlockIndexElement>();
    var totalBlocks = (partitionPayload.Length + this.BlockSize - 1) / this.BlockSize;
    for (var blockIndex = 0; blockIndex < totalBlocks; ++blockIndex) {
      var start = blockIndex * this.BlockSize;
      var len = Math.Min(this.BlockSize, partitionPayload.Length - start);
      // Pad to full BlockSize so all but the last partial block are uniform on
      // disk; the trailing tail length is captured in the JSON
      // partition_byte_size so the reader can truncate on extract.
      var plain = new byte[this.BlockSize];
      partitionPayload.Slice(start, len).CopyTo(plain);

      var rawBytes = plain.AsSpan();
      var md5 = MD5.HashData(rawBytes); // spec: MD5 is of the raw decompressed/decrypted bytes.

      var payload = (byte[])rawBytes.ToArray();
      if (this.CompressDataBlocks)
        payload = CompressZstd(payload);
      if (this.EncryptDataBlocks) {
        var iv = MacriumCrypto.DeriveBlockIv(derivedKey!, imageIdBytes, this.DiskNumber, this.PartitionNumber, blockIndex);
        payload = MacriumCrypto.EncryptBlock(payload, aesKey!, iv);
      }

      var filePos = ms.Position;
      ms.Write(payload, 0, payload.Length);

      indexElements.Add(new DataBlockIndexElement {
        FilePosition = filePos,
        Md5Hash = md5,
        BlockLength = (uint)payload.Length,
        FileNumber = 0,
      });
    }

    // ── 3) Disk metadata chain at index_file_position ─────────────────────
    // Per vendor spec, the disk-level chain (TRACK0/EPT/BITMAP/INDEX) is a
    // separate walk pointed to by _header.index_file_position inside the JSON.
    // The ROOT chain ($JSON + $AUXDATA) is what the footer offset points to,
    // and is walked separately.
    var indexFilePosition = ms.Position;

    // ─── $TRACK0 (uncompressed, never encrypted per spec convention) ─────
    WriteMetadataBlock(ms, "$TRACK0", track0, compressed: false, encrypted: false, last: false);

    // ─── $INDEX (terminal of the disk chain in our minimum-format emission) ─
    // Layout (DataBlockIndex variant — no Reserved-Sectors-Index for non-FAT):
    //   uint32 index_count
    //   DataBlockIndexElement[index_count]
    // Each element = int64 file_position + 16-byte md5 + uint32 block_length + uint16 file_number = 30 bytes.
    var indexBuf = SerializeIndex(indexElements);
    WriteMetadataBlock(ms, "$INDEX", indexBuf, compressed: false, encrypted: false, last: true);

    // ── 4) Root metadata chain at footer offset ───────────────────────────
    var rootChainOffset = ms.Position;

    // ─── $JSON (navigational metadata; zstd-compressed for parity with vendor) ──
    var jsonText = BuildJson(
      imageIdBytes: imageIdBytes,
      indexFilePosition: indexFilePosition,
      diskImage: diskImage,
      partitionByteSize: partitionPayload.Length,
      reservedLen: reservedLen,
      blockCount: totalBlocks,
      hmac: hmac);
    var jsonBytes = Encoding.UTF8.GetBytes(jsonText);
    WriteMetadataBlock(ms, "$JSON", jsonBytes, compressed: true, encrypted: false, last: false);

    // ─── $AUXDATA terminal block of the root chain ────────────────────────
    WriteMetadataBlock(ms, "$AUXDATA", [], compressed: false, encrypted: false, last: true);

    // ── 5) Footer — points at the ROOT chain ──────────────────────────────
    Span<byte> footer = stackalloc byte[20];
    BinaryPrimitives.WriteUInt64LittleEndian(footer[..8], (ulong)rootChainOffset);
    "MACRIUM_FILE"u8.CopyTo(footer[8..]);
    ms.Write(footer);

    return ms.ToArray();
  }

  // ─────────────────────────────────────────────────────────────────────────

  private static byte[] GenerateImageId() {
    var bytes = new byte[MacriumCrypto.ImageIdSize];
    RandomNumberGenerator.Fill(bytes);
    return bytes;
  }

  private static byte[] CompressZstd(byte[] raw) {
    using var input = new MemoryStream(raw, writable: false);
    using var output = new MemoryStream();
    using (var zs = new ZstdStream(output, CompressionStreamMode.Compress, leaveOpen: true))
      input.CopyTo(zs);
    return output.ToArray();
  }

  private static void WriteMetadataBlock(
      Stream output,
      string name,
      ReadOnlySpan<byte> payload,
      bool compressed,
      bool encrypted,
      bool last) {

    // Compress for metadata-side blocks when caller asked. Encryption of
    // metadata blocks is supported by the spec but not exercised here.
    var bodyBytes = payload.ToArray();
    if (compressed)
      bodyBytes = CompressZstd(bodyBytes);

    // 32-byte header: name(8) + length(4 LE) + md5(16) + flags(1) + pad(3).
    Span<byte> header = stackalloc byte[32];

    // Block name, ASCII, padded with spaces to 8 bytes.
    for (var i = 0; i < 8; ++i) header[i] = (byte)' ';
    var nameBytes = Encoding.ASCII.GetBytes(name);
    nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 8)).CopyTo(header[..8]);

    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), (uint)bodyBytes.Length);

    // MD5 of the on-disk payload (per spec language: "MD5 hash of the block").
    // The DataBlockIndexElement MD5 is the post-decrypt/decompress hash;
    // this header MD5 is over the bytes as written.
    var hash = MD5.HashData(bodyBytes);
    hash.CopyTo(header.Slice(12, 16));

    byte flags = 0;
    if (last) flags |= 0x01;
    if (compressed) flags |= 0x02;
    if (encrypted) flags |= 0x04;
    header[28] = flags;
    // header[29..32] = padding zeroes (already zero).

    output.Write(header);
    output.Write(bodyBytes);
  }

  private static byte[] SerializeIndex(IReadOnlyList<DataBlockIndexElement> elements) {
    // uint32 index_count + N × (int64 + md5[16] + uint32 + uint16) = 4 + N*30 bytes.
    var size = 4 + elements.Count * 30;
    var buf = new byte[size];
    var span = buf.AsSpan();
    BinaryPrimitives.WriteUInt32LittleEndian(span[..4], (uint)elements.Count);
    var offset = 4;
    foreach (var e in elements) {
      BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, 8), e.FilePosition);
      offset += 8;
      e.Md5Hash.CopyTo(span.Slice(offset, 16));
      offset += 16;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), e.BlockLength);
      offset += 4;
      BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), e.FileNumber);
      offset += 2;
    }
    return buf;
  }

  private string BuildJson(
      byte[] imageIdBytes,
      long indexFilePosition,
      ReadOnlySpan<byte> diskImage,
      int partitionByteSize,
      int reservedLen,
      int blockCount,
      byte[]? hmac) {

    // Macrium imageid in JSON is uppercase hex of the 8 raw bytes.
    var imageIdHex = string.Concat(imageIdBytes.Select(b => b.ToString("X2")));
    var backupTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    var sb = new StringBuilder();
    sb.Append('{');

    // _compression — track what the writer actually applied to data blocks
    // so the reader's $INDEX walk knows whether to invoke zstd-decompress.
    if (this.CompressDataBlocks)
      sb.Append("\"_compression\":{\"compression_level\":\"medium\",\"compression_method\":\"zstd\"},");
    else
      sb.Append("\"_compression\":{\"compression_level\":\"none\",\"compression_method\":\"none\"},");

    // _encryption block — present and enabled when password supplied.
    sb.Append("\"_encryption\":{");
    if (this.EncryptDataBlocks && hmac is not null) {
      sb.Append("\"aes_type\":\"").Append(this.AesType switch {
        MacriumAesType.Aes128 => "aes-128",
        MacriumAesType.Aes192 => "aes-192",
        _ => "aes-256",
      }).Append("\",");
      sb.Append("\"enable\":true,");
      sb.Append("\"hmac\":\"").Append(MacriumCrypto.BytesToHex(hmac)).Append("\",");
      sb.Append("\"key_derivation\":\"pbkdf2\",");
      sb.Append("\"key_iterations\":").Append(this.Pbkdf2Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
    } else {
      sb.Append("\"enable\":false,\"key_iterations\":0");
    }
    sb.Append("},");

    // _header — navigational fields used by readers.
    sb.Append("\"_header\":{");
    sb.Append("\"backup_format\":\"partition\",");
    sb.Append("\"backup_guid\":\"00000000-0000-0000-0000-000000000000\",");
    sb.Append("\"backup_time\":").Append(backupTime.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
    sb.Append("\"backup_type\":\"full\",");
    sb.Append("\"backupset_time\":").Append(backupTime.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
    sb.Append("\"delta_index\":false,");
    sb.Append("\"file_number\":0,");
    sb.Append("\"imaged_disks_count\":1,");
    sb.Append("\"imageid\":\"").Append(imageIdHex).Append("\",");
    sb.Append("\"increment_number\":0,");
    sb.Append("\"index_file_position\":").Append(indexFilePosition.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
    sb.Append("\"json_version\":1,");
    sb.Append("\"netbios_name\":\"compression-workbench\",");
    sb.Append("\"split_file\":false");
    sb.Append("},");

    // disks[0]._header / partitions[0]._header — minimum required by spec.
    sb.Append("\"disks\":[{");
    sb.Append("\"_header\":{");
    sb.Append("\"disk_format\":\"raw\",");
    sb.Append("\"disk_number\":").Append(this.DiskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
    sb.Append("\"disk_signature\":\"00000000-0000-0000-0000-000000000000\",");
    sb.Append("\"imaged_partition_count\":1");
    sb.Append("},");
    sb.Append("\"partitions\":[{");
    sb.Append("\"_header\":{");
    sb.Append("\"block_count\":").Append(blockCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
    sb.Append("\"block_size\":").Append(this.BlockSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
    sb.Append("\"file_history\":[],");
    sb.Append("\"file_history_count\":0,");
    sb.Append("\"partition_file_offset\":0,");
    sb.Append("\"partition_number\":").Append(this.PartitionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
    sb.Append("},");
    // Extension: capture trailing-block tail length so the reader can extract
    // the exact original byte count on round-trip. Spec doesn't standardize a
    // field for this — Macrium recovers it via per-partition byte count
    // elsewhere — so we use a vendor-extension key prefixed with "_cw_".
    sb.Append("\"_cw_extra\":{");
    sb.Append("\"partition_byte_size\":").Append(partitionByteSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
    sb.Append("\"reserved_sectors_length\":").Append(reservedLen.ToString(System.Globalization.CultureInfo.InvariantCulture));
    sb.Append('}');
    sb.Append("}]");
    sb.Append("}]");
    sb.Append('}');
    return sb.ToString();
  }

  /// <summary>Per-block index element exactly as the spec describes (struct DataBlockIndexElement: int64 file_position + uint8[16] md5_hash + uint32 block_length + uint16 file_number = 30 bytes packed).</summary>
  internal sealed class DataBlockIndexElement {
    public long FilePosition;
    public byte[] Md5Hash = new byte[16];
    public uint BlockLength;
    public ushort FileNumber;
  }
}

/// <summary>
/// AES variant selector. The numeric value is the key length in bytes
/// (truncation of the 32-byte PBKDF2 derived key).
/// </summary>
public enum MacriumAesType {
  /// <summary>AES-128-CBC — first 16 bytes of derived key are used.</summary>
  Aes128 = 16,
  /// <summary>AES-192-CBC — first 24 bytes of derived key are used.</summary>
  Aes192 = 24,
  /// <summary>AES-256-CBC — full 32-byte derived key is used.</summary>
  Aes256 = 32,
}
