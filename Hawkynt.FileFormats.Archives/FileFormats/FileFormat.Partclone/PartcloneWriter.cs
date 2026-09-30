#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Partclone;

/// <summary>
/// Writes partclone image format 0002 from a raw partition image, in the layout documented on
/// <see cref="PartcloneReader"/>. Output is accepted by partclone 0.3.27's
/// <c>partclone.chkimg</c> and restored byte-identically by <c>partclone.restore</c>.
/// </summary>
public static class PartcloneWriter {
  private static readonly UTF8Encoding Utf8 = new(false);

  /// <summary>The partclone release whose on-disk output this writer reproduces.</summary>
  public const string DefaultPtcVersion = "0.3.27";

  // partclone's default copy buffer; with no -k it checksums one buffer's worth of blocks.
  private const int DefaultBufferSize = 1 << 20;
  private const uint MaxBlockSize = 64u << 20;

  /// <summary>
  /// Writes an image of <paramref name="disk"/>.
  /// </summary>
  /// <param name="output">Destination.</param>
  /// <param name="disk">Raw partition, readable and seekable, exactly <c>total_blocks × block_size</c> long.</param>
  /// <param name="metadata">A <c>metadata.ini</c> as extracted from a partclone image; may be empty.</param>
  /// <param name="allocationMap">
  /// The serialized allocation map as extracted (bit or byte form, per <c>bitmap_mode</c> in the
  /// metadata, else inferred from its length). Empty derives the map from the non-zero blocks, which
  /// cannot tell an allocated all-zero block from a free one.
  /// </param>
  /// <param name="overrides">Option overrides: Fs, BlockSize, BitmapMode (none/bit/byte), ChecksumMode (none/crc32), BlocksPerChecksum, ReseedChecksum.</param>
  public static void Write(Stream output, Stream disk, ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> allocationMap,
    IReadOnlyDictionary<string, string>? overrides = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(disk);
    if (!output.CanWrite) throw new ArgumentException("Partclone output must be writable.", nameof(output));
    if (!disk.CanRead || !disk.CanSeek) throw new ArgumentException("Partclone input must be readable and seekable.", nameof(disk));

    var meta = ParseMetadata(metadata);
    string? Option(string optionKey, string metaKey)
      => overrides is not null && overrides.TryGetValue(optionKey, out var o) ? o
        : meta.TryGetValue(metaKey, out var m) ? m : null;

    var blockSize = ParseUInt32(Option("BlockSize", "block_size") ?? "4096", "block_size");
    if (blockSize is 0 or > MaxBlockSize)
      throw new ArgumentException($"Partclone block_size must be 1..{MaxBlockSize}.", nameof(metadata));

    var totalBlocks = meta.TryGetValue("total_blocks", out var tb) ? ParseUInt64(tb, "total_blocks") : 0UL;
    if (totalBlocks == 0) {
      if (disk.Length == 0 || disk.Length % blockSize != 0)
        throw new ArgumentException("Raw image length must be a non-zero multiple of block_size.", nameof(disk));
      totalBlocks = (ulong)disk.Length / blockSize;
    }
    if ((ulong)disk.Length != checked(totalBlocks * blockSize))
      throw new ArgumentException($"Raw image is {disk.Length} bytes; metadata declares {totalBlocks} × {blockSize}.", nameof(disk));
    if (totalBlocks > int.MaxValue)
      throw new NotSupportedException("Partclone images beyond 2^31 blocks are not supported.");

    var bitmapMode = ParseBitmapMode(Option("BitmapMode", "bitmap_mode") ?? "1");
    // partclone writes a CRC after a 0002 byte map but reads one back expecting the 0001 "BiTmAgIc"
    // trailer, so no partclone release can restore such an image; it only ever writes bit maps.
    if (bitmapMode == PartcloneReader.BmByte)
      throw new NotSupportedException("Partclone 0002 images with a byte map cannot be restored by partclone; use the bit map.");
    var bits = BuildUsageBits(disk, allocationMap, meta, totalBlocks, blockSize, bitmapMode);
    var usedBlocks = bitmapMode == PartcloneReader.BmNone ? totalBlocks : CountBits(bits, totalBlocks);

    var checksumMode = ParseChecksumMode(Option("ChecksumMode", "checksum_mode") ?? "crc32");
    // XXH64 strips are read, but not written: the partclone builds available as a reference
    // (Debian/Ubuntu) are compiled without xxhash, so such output could not be verified.
    if (checksumMode == PartcloneReader.CsXxh64)
      throw new NotSupportedException("Writing XXH64 partclone checksums is not supported; use crc32 or none.");
    var checksumSize = checksumMode == PartcloneReader.CsNone ? (ushort)0 : (ushort)4;
    var defaultStrip = blockSize < DefaultBufferSize ? (uint)(DefaultBufferSize / blockSize) : 1u;
    var blocksPerChecksum = checksumMode == PartcloneReader.CsNone
      ? 0u
      : ParseUInt32(Option("BlocksPerChecksum", "blocks_per_checksum") ?? defaultStrip.ToString(CultureInfo.InvariantCulture), "blocks_per_checksum");
    if (checksumMode != PartcloneReader.CsNone && blocksPerChecksum == 0) blocksPerChecksum = defaultStrip;
    var reseed = ParseUInt32(Option("ReseedChecksum", "reseed_checksum") ?? "1", "reseed_checksum") != 0;

    var fs = Option("Fs", "fs") ?? "raw";
    var ptcVersion = meta.GetValueOrDefault("ptc_version", DefaultPtcVersion);
    var deviceSize = meta.TryGetValue("device_size", out var ds) ? ParseUInt64(ds, "device_size") : totalBlocks * blockSize;
    var superBlockUsed = meta.TryGetValue("superblock_used_blocks", out var su) ? ParseUInt64(su, "superblock_used_blocks") : usedBlocks;
    var cpuBits = ParseUInt16(meta.GetValueOrDefault("cpu_bits", "64"), "cpu_bits");

    // Image descriptor.
    Span<byte> h = stackalloc byte[PartcloneReader.HeaderSize];
    h.Clear();
    PartcloneReader.Magic.CopyTo(h);
    WriteAscii(h.Slice(PartcloneReader.MagicFieldSize, PartcloneReader.VersionSizeV2), ptcVersion, "ptc_version");
    "0002"u8.CopyTo(h[30..]);
    BinaryPrimitives.WriteUInt16LittleEndian(h[34..], PartcloneReader.EndianMagic);
    WriteAscii(h.Slice(36, PartcloneReader.FsFieldSize - 1), fs, "fs");
    BinaryPrimitives.WriteUInt64LittleEndian(h[52..], deviceSize);
    BinaryPrimitives.WriteUInt64LittleEndian(h[60..], totalBlocks);
    BinaryPrimitives.WriteUInt64LittleEndian(h[68..], superBlockUsed);
    BinaryPrimitives.WriteUInt64LittleEndian(h[76..], usedBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(h[84..], blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(h[88..], PartcloneReader.FeatureSize);
    BinaryPrimitives.WriteUInt16LittleEndian(h[92..], 2);
    BinaryPrimitives.WriteUInt16LittleEndian(h[94..], cpuBits);
    BinaryPrimitives.WriteUInt16LittleEndian(h[96..], checksumMode);
    BinaryPrimitives.WriteUInt16LittleEndian(h[98..], checksumSize);
    BinaryPrimitives.WriteUInt32LittleEndian(h[100..], blocksPerChecksum);
    h[104] = reseed ? (byte)1 : (byte)0;
    h[105] = bitmapMode;
    BinaryPrimitives.WriteUInt32LittleEndian(h[PartcloneReader.HeaderCrcOffset..], PartcloneReader.PartcloneCrc32(h[..PartcloneReader.HeaderCrcOffset]));
    output.Write(h);

    // Bit map, then its CRC.
    if (bitmapMode == PartcloneReader.BmBit) {
      output.Write(bits);
      WriteUInt32(output, PartcloneReader.PartcloneCrc32(bits));
    }

    // Used blocks in strips of blocks_per_checksum, each followed by its checksum; a final
    // partial strip gets one too.
    disk.Position = 0;
    var block = new byte[blockSize];
    var crc = new Crc32();
    var inStrip = 0u;
    for (ulong i = 0; i < totalBlocks; ++i) {
      disk.ReadExactly(block);
      if (bitmapMode != PartcloneReader.BmNone && (bits[(int)(i >> 3)] & (1 << (int)(i & 7))) == 0) continue;
      output.Write(block);
      if (checksumMode == PartcloneReader.CsNone) continue;
      crc.Update(block);
      if (++inStrip < blocksPerChecksum) continue;
      WriteChecksum();
    }
    if (inStrip > 0) WriteChecksum();

    void WriteChecksum() {
      WriteUInt32(output, ~crc.Value);
      inStrip = 0;
      if (reseed) crc.Reset();
    }
  }

  private static byte[] BuildUsageBits(Stream disk, ReadOnlySpan<byte> allocationMap, Dictionary<string, string> meta,
      ulong totalBlocks, uint blockSize, byte targetMode) {
    var bits = new byte[(totalBlocks + 7) / 8];
    if (targetMode == PartcloneReader.BmNone) return bits;

    if (!allocationMap.IsEmpty) {
      var bitLength = (int)((totalBlocks + 7) / 8);
      byte sourceMode;
      if (meta.TryGetValue("bitmap_mode", out var declared))
        sourceMode = ParseBitmapMode(declared);
      else if (allocationMap.Length == bitLength) sourceMode = PartcloneReader.BmBit;
      else if ((ulong)allocationMap.Length == totalBlocks) sourceMode = PartcloneReader.BmByte;
      else throw new ArgumentException("allocation.map length matches neither a bit nor a byte map of total_blocks.");

      switch (sourceMode) {
        case PartcloneReader.BmBit when allocationMap.Length == bitLength:
          allocationMap.CopyTo(bits);
          // Bits past total_blocks are not blocks; never let them count.
          if (totalBlocks % 8 != 0) bits[^1] &= (byte)((1 << (int)(totalBlocks % 8)) - 1);
          return bits;
        case PartcloneReader.BmByte when (ulong)allocationMap.Length == totalBlocks:
          for (var i = 0; i < allocationMap.Length; ++i)
            if (allocationMap[i] != 0) bits[i >> 3] |= (byte)(1 << (i & 7));
          return bits;
        case PartcloneReader.BmNone:
          break; // the source had no map; fall through to "every block used"
        default:
          throw new ArgumentException($"allocation.map length {allocationMap.Length} does not match bitmap_mode {sourceMode} for {totalBlocks} blocks.");
      }
      for (ulong i = 0; i < totalBlocks; ++i) bits[i >> 3] |= (byte)(1 << (int)(i & 7));
      return bits;
    }

    var block = new byte[blockSize];
    disk.Position = 0;
    for (ulong i = 0; i < totalBlocks; ++i) {
      disk.ReadExactly(block);
      if (block.AsSpan().IndexOfAnyExcept((byte)0) >= 0) bits[i >> 3] |= (byte)(1 << (int)(i & 7));
    }
    return bits;
  }

  private static ulong CountBits(byte[] bits, ulong totalBlocks) {
    ulong count = 0;
    for (ulong i = 0; i < totalBlocks; ++i)
      if ((bits[i >> 3] & (1 << (int)(i & 7))) != 0) ++count;
    return count;
  }

  // Accepts partclone's -a numbers (0/1/2), the on-disk enum values (0/32/48) that metadata.ini
  // carries, and the names.
  private static ushort ParseChecksumMode(string value) => value.Trim().ToLowerInvariant() switch {
    "0" or "none" => PartcloneReader.CsNone,
    "1" or "32" or "crc32" => PartcloneReader.CsCrc32,
    "2" or "48" or "xxh64" => PartcloneReader.CsXxh64,
    var other => throw new NotSupportedException($"Partclone checksum mode '{other}' is not supported; use none, crc32 or xxh64."),
  };

  private static byte ParseBitmapMode(string value) => value.Trim().ToLowerInvariant() switch {
    "0" or "none" => PartcloneReader.BmNone,
    "1" or "bit" => PartcloneReader.BmBit,
    "8" or "byte" => PartcloneReader.BmByte,
    var other => throw new ArgumentException($"Partclone bitmap mode '{other}' is not one of 0 (none), 1 (bit), 8 (byte)."),
  };

  private static void WriteAscii(Span<byte> field, string value, string name) {
    var bytes = Encoding.ASCII.GetBytes(value);
    if (bytes.Length > field.Length)
      throw new ArgumentException($"Partclone {name} '{value}' exceeds {field.Length} bytes.");
    bytes.CopyTo(field);
  }

  private static Dictionary<string, string> ParseMetadata(ReadOnlySpan<byte> bytes) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in Utf8.GetString(bytes).Split('\n')) {
      var text = line.Trim();
      if (text.Length == 0 || text[0] is '#' or ';' or '[') continue;
      var separator = text.IndexOf('=');
      if (separator > 0) result[text[..separator].Trim()] = text[(separator + 1)..].Trim();
    }
    return result;
  }

  private static ulong ParseUInt64(string value, string name)
    => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r
      : throw new ArgumentException($"Partclone {name} '{value}' is not an unsigned integer.");

  private static uint ParseUInt32(string value, string name)
    => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r
      : throw new ArgumentException($"Partclone {name} '{value}' is not an unsigned integer.");

  private static ushort ParseUInt16(string value, string name)
    => ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r
      : throw new ArgumentException($"Partclone {name} '{value}' is not an unsigned 16-bit integer.");

  private static void WriteUInt32(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
  }

}
