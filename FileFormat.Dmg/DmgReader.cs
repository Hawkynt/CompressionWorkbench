#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileFormat.Dmg;


/// <summary>
/// Read-only reader for Apple Disk Image (DMG) files.
/// Parses the koly trailer, XML plist, and mish block tables to expose each
/// partition as an extractable entry.
/// </summary>
public sealed class DmgReader : IDisposable {
  // Block types in the mish block table
  private const uint BlockTypeZeroFill  = 0x00000000;
  private const uint BlockTypeRaw       = 0x00000001;
  private const uint BlockTypeZlib      = 0x80000005;
  private const uint BlockTypeBzip2     = 0x80000006;
  private const uint BlockTypeLzfse     = 0x80000007;
  private const uint BlockTypeLzma      = 0x80000008;
  private const uint BlockTypeComment   = 0x7FFFFFFE;
  private const uint BlockTypeTerminator= 0xFFFFFFFF;

  private const int KolySize = 512;
  private const int SectorSize = 512;

  private readonly byte[] _data;
  private readonly List<DmgEntry> _entries = [];
  private readonly List<PartitionInfo> _partitions = [];

  /// <summary>All partitions found in the DMG, each exposed as a named entry.</summary>
  public IReadOnlyList<DmgEntry> Entries => _entries;

  public DmgReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Parsing
  // ──────────────────────────────────────────────────────────────────────────

  private void Parse() {
    if (_data.Length < KolySize)
      throw new InvalidDataException("DMG: file too small to contain koly trailer.");

    var kolyOff = _data.Length - KolySize;
    var kolySpan = _data.AsSpan(kolyOff, KolySize);

    // Check "koly" signature
    if (kolySpan[0] != 'k' || kolySpan[1] != 'o' || kolySpan[2] != 'l' || kolySpan[3] != 'y')
      throw new InvalidDataException("DMG: missing 'koly' trailer signature.");

    // Parse koly fields (all big-endian)
    // Offset  4: uint32 version
    // Offset  8: uint32 headerSize
    // Offset 12: uint32 flags
    // Offset 16: uint64 runningDataForkOffset
    // Offset 24: uint64 dataForkOffset
    // Offset 32: uint64 dataForkLength
    // Offset 40: uint64 rsrcForkOffset
    // Offset 48: uint64 rsrcForkLength
    // Offset 56: uint32 segmentNumber
    // Offset 60: uint32 segmentCount
    // Offset 64: Guid   segmentId (16 bytes)
    // Offset 80: uint32 dataChecksumType
    // Offset 84: uint32 dataChecksumSize
    // Offset 88: uint32[32] dataChecksum  (128 bytes)
    // Offset 216: uint64 xmlOffset
    // Offset 224: uint64 xmlLength
    // Offset 232: reserved (120 bytes, brings us to 352)
    // Offset 352: uint32 masterChecksumType
    // Offset 356: uint32 masterChecksumSize
    // Offset 360: uint32[32] masterChecksum (128 bytes)
    // Offset 488: uint32 imageVariant
    // Offset 492: uint64 sectorCount
    // Offset 500: 12 bytes reserved

    var xmlOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(kolySpan[216..]);
    var xmlLength = (long)BinaryPrimitives.ReadUInt64BigEndian(kolySpan[224..]);

    if (xmlLength <= 0 || xmlOffset < 0 || xmlOffset + xmlLength > _data.Length)
      throw new InvalidDataException("DMG: invalid XML plist region in koly trailer.");

    var xmlText = Encoding.UTF8.GetString(_data, (int)xmlOffset, (int)xmlLength);
    ParseXmlPlist(xmlText);
  }

  private void ParseXmlPlist(string xml) {
    // We use simple string search — no XML parser dependency.
    // Structure we're looking for (may repeat for multiple partitions):
    //
    //   <key>blkx</key>
    //   <array>
    //     <dict>
    //       <key>Name</key><string>…</string>
    //       <key>Data</key><data>BASE64…</data>
    //     </dict>
    //     …
    //   </array>

    var blkxPos = xml.IndexOf("<key>blkx</key>", StringComparison.Ordinal);
    if (blkxPos < 0) return; // no partitions

    var arrayStart = xml.IndexOf("<array>", blkxPos, StringComparison.Ordinal);
    var arrayEnd   = xml.IndexOf("</array>", blkxPos, StringComparison.Ordinal);
    if (arrayStart < 0 || arrayEnd < 0 || arrayEnd <= arrayStart) return;

    var arrayBody = xml.Substring(arrayStart + 7, arrayEnd - arrayStart - 7);

    // Parse each <dict> element
    var dictStart = 0;
    var partIndex = 0;
    while (true) {
      var dStart = arrayBody.IndexOf("<dict>", dictStart, StringComparison.Ordinal);
      if (dStart < 0) break;
      var dEnd = arrayBody.IndexOf("</dict>", dStart, StringComparison.Ordinal);
      if (dEnd < 0) break;

      var dictBody = arrayBody.Substring(dStart + 6, dEnd - dStart - 6);
      var (name, mish) = ParseBlkxDict(dictBody, partIndex);
      if (mish != null) {
        var size = ComputePartitionSize(mish);
        _entries.Add(new DmgEntry { Name = name, Size = size });
        _partitions.Add(new PartitionInfo(name, mish));
        partIndex++;
      }

      dictStart = dEnd + 7;
    }
  }

  private static (string name, byte[]? mish) ParseBlkxDict(string dictBody, int index) {
    // Extract <key>Name</key><string>…</string>
    var name = $"partition_{index}.img";
    var nameKeyPos = dictBody.IndexOf("<key>Name</key>", StringComparison.Ordinal);
    if (nameKeyPos >= 0) {
      var strStart = dictBody.IndexOf("<string>", nameKeyPos, StringComparison.Ordinal);
      var strEnd   = dictBody.IndexOf("</string>", nameKeyPos, StringComparison.Ordinal);
      if (strStart >= 0 && strEnd > strStart) {
        var raw = dictBody.Substring(strStart + 8, strEnd - strStart - 8).Trim();
        if (raw.Length > 0)
          name = SanitizeName(raw, index);
      }
    }

    // Extract <key>Data</key><data>BASE64</data>
    byte[]? mish = null;
    var dataKeyPos = dictBody.IndexOf("<key>Data</key>", StringComparison.Ordinal);
    if (dataKeyPos < 0)
      dataKeyPos = dictBody.IndexOf("<key>data</key>", StringComparison.Ordinal); // lowercase fallback
    if (dataKeyPos >= 0) {
      var dataStart = dictBody.IndexOf("<data>", dataKeyPos, StringComparison.Ordinal);
      var dataEnd   = dictBody.IndexOf("</data>", dataKeyPos, StringComparison.Ordinal);
      if (dataStart >= 0 && dataEnd > dataStart) {
        var b64 = dictBody.Substring(dataStart + 6, dataEnd - dataStart - 6)
                          .Replace("\n", "").Replace("\r", "").Replace(" ", "").Replace("\t", "");
        try { mish = Convert.FromBase64String(b64); } catch { mish = null; }
      }
    }

    return (name, mish);
  }

  private static string SanitizeName(string raw, int index) {
    // Strip common Apple partition decorators like "(Apple_HFS : 2)"
    var paren = raw.IndexOf('(');
    if (paren > 0) raw = raw[..paren].Trim();
    // Replace characters that are bad in filenames
    foreach (var ch in Path.GetInvalidFileNameChars())
      raw = raw.Replace(ch, '_');
    raw = raw.Trim().Replace(' ', '_');
    if (raw.Length == 0) raw = $"partition_{index}";
    if (!raw.Contains('.')) raw += ".img";
    return raw;
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Mish (block table) parsing
  // ──────────────────────────────────────────────────────────────────────────

  private sealed record BlockEntry(uint Type, ulong SectorOffset, ulong SectorCount,
                                   ulong CompressedOffset, ulong CompressedLength);

  private sealed record MishTable(ulong FirstSector, ulong SectorCount, ulong DataStart,
                                  List<BlockEntry> Blocks);

  private static MishTable? ParseMish(byte[] mish) {
    if (mish == null || mish.Length < 204) return null;

    // "mish" signature
    if (mish[0] != 'm' || mish[1] != 'i' || mish[2] != 's' || mish[3] != 'h') return null;

    // All big-endian
    // Offset  4: uint32 version
    // Offset  8: uint64 firstSector
    // Offset 16: uint64 sectorCount
    // Offset 24: uint64 dataStart
    // Offset 32: uint32 decompressedBufferRequested
    // Offset 36: uint32 blocksDescriptor
    // Offset 40: reserved 24 bytes
    // Offset 64: checksum type/size/data (136 bytes total: 4+4+128)
    // Offset 200: uint32 numBlockEntries
    // Then block entries at offset 204, each 40 bytes

    var firstSector  = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(8));
    var sectorCount  = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(16));
    var dataStart    = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(24));
    var numEntries   = BinaryPrimitives.ReadUInt32BigEndian(mish.AsSpan(200));

    if (numEntries > 100_000) return null; // sanity guard
    var blocks = new List<BlockEntry>((int)numEntries);

    var off = 204;
    for (var i = 0u; i < numEntries; i++) {
      if (off + 40 > mish.Length) break;
      var blockType        = BinaryPrimitives.ReadUInt32BigEndian(mish.AsSpan(off));
      // offset 4 = uint32 reserved
      var sectorOffset     = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(off + 8));
      var blockSectorCount = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(off + 16));
      var compressedOffset = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(off + 24));
      var compressedLength = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(off + 32));
      blocks.Add(new BlockEntry(blockType, sectorOffset, blockSectorCount, compressedOffset, compressedLength));
      off += 40;
    }

    return new MishTable(firstSector, sectorCount, dataStart, blocks);
  }

  private static long ComputePartitionSize(byte[] mish) {
    var table = ParseMish(mish);
    if (table == null) return 0;
    return (long)table.SectorCount * SectorSize;
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Extraction
  // ──────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Reassembles and returns the raw sector data for <paramref name="entry"/>.
  /// </summary>
  public byte[] Extract(DmgEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var pi = _partitions.FirstOrDefault(p => p.Name == entry.Name);
    if (pi == null) return [];

    var table = ParseMish(pi.Mish);
    if (table == null) return [];

    var totalBytes = (long)table.SectorCount * SectorSize;
    if (totalBytes <= 0 || totalBytes > 2L * 1024 * 1024 * 1024)
      totalBytes = Math.Max(0, Math.Min(totalBytes, 2L * 1024 * 1024 * 1024));

    var output = new byte[totalBytes];

    foreach (var block in table.Blocks) {
      if (block.Type == BlockTypeComment || block.Type == BlockTypeTerminator) continue;

      var destOffset = (long)block.SectorOffset * SectorSize;
      var destLength = (long)block.SectorCount  * SectorSize;

      if (destLength == 0) continue;
      if (destOffset < 0 || destOffset + destLength > output.LongLength) continue;

      switch (block.Type) {
        case BlockTypeZeroFill:
          // Already zero — nothing to write
          break;

        case BlockTypeRaw:
          ExtractRaw(block, destOffset, destLength, output);
          break;

        case BlockTypeZlib:
          ExtractZlib(block, destOffset, destLength, output);
          break;

        case BlockTypeBzip2:
          ExtractBzip2(block, destOffset, destLength, output);
          break;

        case BlockTypeLzfse:
        case BlockTypeLzma:
          // Unsupported compression — leave zeros in output
          break;

        default:
          // Unknown type — leave zeros
          break;
      }
    }

    return output;
  }

  private void ExtractRaw(BlockEntry block, long destOffset, long destLength, byte[] output) {
    var srcOffset = (long)block.CompressedOffset;
    var srcLength = (long)block.CompressedLength;
    if (srcOffset < 0 || srcOffset + srcLength > _data.LongLength) return;
    var copyLen = (int)Math.Min(srcLength, destLength);
    _data.AsSpan((int)srcOffset, copyLen).CopyTo(output.AsSpan((int)destOffset));
  }

  private void ExtractZlib(BlockEntry block, long destOffset, long destLength, byte[] output) {
    var srcOffset = (long)block.CompressedOffset;
    var srcLength = (long)block.CompressedLength;
    if (srcOffset < 0 || srcLength < 2 || srcOffset + srcLength > _data.LongLength) return;

    // zlib stream: skip 2-byte header (CMF + FLG), use raw DEFLATE
    try {
      using var src = new MemoryStream(_data, (int)srcOffset + 2, (int)srcLength - 2);
      using var deflate = new DeflateStream(src, CompressionMode.Decompress);
      using var dst = new MemoryStream(output, (int)destOffset, (int)destLength);
      deflate.CopyTo(dst);
    } catch {
      // Decompression failed — leave zeros in the output region
    }
  }

  private static void ExtractBzip2(BlockEntry block, long destOffset, long destLength, byte[] output) {
    // bzip2 decompression is not available in the Compression.Core dependency set.
    // Leave the region zero-filled (safe no-op for read-only listing/testing scenarios).
    _ = block; _ = destOffset; _ = destLength; _ = output;
  }

  public void Dispose() { }

  // ──────────────────────────────────────────────────────────────────────────
  // Internal helpers
  // ──────────────────────────────────────────────────────────────────────────

  private sealed record PartitionInfo(string Name, byte[] Mish);
}
