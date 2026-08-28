#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace FileFormat.Dmg;

/// <summary>
/// Reader for Apple Disk Image (DMG/UDIF) files. Parses the koly trailer,
/// XML plist, and mish block tables to expose each partition as an entry.
/// </summary>
public sealed class DmgReader : IDisposable {
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

  internal long XmlOffset { get; private set; }
  internal long XmlLength { get; private set; }
  internal byte[] KolyTrailer { get; private set; } = [];
  internal IReadOnlyList<PartitionInfo> Partitions => _partitions;

  public DmgReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
    _ = leaveOpen;
  }

  private void Parse() {
    if (_data.Length < KolySize)
      throw new InvalidDataException("DMG: file too small to contain koly trailer.");

    var kolyOff = _data.Length - KolySize;
    var kolySpan = _data.AsSpan(kolyOff, KolySize);
    if (!kolySpan[..4].SequenceEqual("koly"u8))
      throw new InvalidDataException("DMG: missing 'koly' trailer signature.");

    XmlOffset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(kolySpan[216..]));
    XmlLength = checked((long)BinaryPrimitives.ReadUInt64BigEndian(kolySpan[224..]));
    KolyTrailer = kolySpan.ToArray();

    if (XmlLength <= 0 || XmlOffset < 0 || XmlOffset + XmlLength > kolyOff)
      throw new InvalidDataException("DMG: invalid XML plist region in koly trailer.");

    var xmlText = Encoding.UTF8.GetString(_data, (int)XmlOffset, (int)XmlLength);
    ParseXmlPlist(xmlText);
  }

  private void ParseXmlPlist(string xml) {
    var blkxPos = xml.IndexOf("<key>blkx</key>", StringComparison.Ordinal);
    if (blkxPos < 0) return;

    var arrayStart = xml.IndexOf("<array>", blkxPos, StringComparison.Ordinal);
    var arrayEnd = xml.IndexOf("</array>", blkxPos, StringComparison.Ordinal);
    if (arrayStart < 0 || arrayEnd < 0 || arrayEnd <= arrayStart) return;

    var arrayBody = xml.Substring(arrayStart + 7, arrayEnd - arrayStart - 7);
    var dictStart = 0;
    var partIndex = 0;
    while (true) {
      var dStart = arrayBody.IndexOf("<dict>", dictStart, StringComparison.Ordinal);
      if (dStart < 0) break;
      var dEnd = arrayBody.IndexOf("</dict>", dStart, StringComparison.Ordinal);
      if (dEnd < 0) break;

      var dictBody = arrayBody.Substring(dStart + 6, dEnd - dStart - 6);
      var parsed = ParseBlkxDict(dictBody, partIndex);
      if (parsed.Mish != null) {
        var physicalSize = ComputePartitionSize(parsed.Mish);
        var logicalSize = parsed.LogicalSize is >= 0 and <= long.MaxValue
          ? Math.Min(parsed.LogicalSize.Value, physicalSize)
          : physicalSize;
        _entries.Add(new DmgEntry { Name = parsed.Name, Size = logicalSize });
        _partitions.Add(new PartitionInfo(parsed.Name, parsed.Mish, logicalSize));
        partIndex++;
      }

      dictStart = dEnd + 7;
    }
  }

  private static (string Name, byte[]? Mish, long? LogicalSize) ParseBlkxDict(string dictBody, int index) {
    var name = $"partition_{index}.img";
    var nameKeyPos = dictBody.IndexOf("<key>Name</key>", StringComparison.Ordinal);
    if (nameKeyPos >= 0) {
      var strStart = dictBody.IndexOf("<string>", nameKeyPos, StringComparison.Ordinal);
      var strEnd = dictBody.IndexOf("</string>", nameKeyPos, StringComparison.Ordinal);
      if (strStart >= 0 && strEnd > strStart) {
        var raw = WebUtility.HtmlDecode(dictBody.Substring(strStart + 8, strEnd - strStart - 8).Trim());
        if (raw.Length > 0) name = SanitizeName(raw, index);
      }
    }

    long? logicalSize = null;
    var logicalKey = dictBody.IndexOf("<key>CWBLogicalSize</key>", StringComparison.Ordinal);
    if (logicalKey >= 0) {
      var valueStart = dictBody.IndexOf("<integer>", logicalKey, StringComparison.Ordinal);
      var valueEnd = dictBody.IndexOf("</integer>", logicalKey, StringComparison.Ordinal);
      if (valueStart >= 0 && valueEnd > valueStart &&
          long.TryParse(dictBody.AsSpan(valueStart + 9, valueEnd - valueStart - 9), out var parsed) && parsed >= 0)
        logicalSize = parsed;
    }

    byte[]? mish = null;
    var dataKeyPos = dictBody.IndexOf("<key>Data</key>", StringComparison.Ordinal);
    if (dataKeyPos < 0)
      dataKeyPos = dictBody.IndexOf("<key>data</key>", StringComparison.Ordinal);
    if (dataKeyPos >= 0) {
      var dataStart = dictBody.IndexOf("<data>", dataKeyPos, StringComparison.Ordinal);
      var dataEnd = dictBody.IndexOf("</data>", dataKeyPos, StringComparison.Ordinal);
      if (dataStart >= 0 && dataEnd > dataStart) {
        var b64 = dictBody.Substring(dataStart + 6, dataEnd - dataStart - 6)
                          .Replace("\n", "").Replace("\r", "").Replace(" ", "").Replace("\t", "");
        try { mish = Convert.FromBase64String(b64); } catch { mish = null; }
      }
    }

    return (name, mish, logicalSize);
  }

  private static string SanitizeName(string raw, int index) {
    var paren = raw.IndexOf('(');
    if (paren > 0) raw = raw[..paren].Trim();
    foreach (var ch in Path.GetInvalidFileNameChars()) raw = raw.Replace(ch, '_');
    raw = raw.Trim().Replace(' ', '_');
    if (raw.Length == 0) raw = $"partition_{index}";
    if (!raw.Contains('.')) raw += ".img";
    return raw;
  }

  internal sealed record BlockEntry(uint Type, ulong SectorOffset, ulong SectorCount,
                                    ulong CompressedOffset, ulong CompressedLength);

  internal sealed record MishTable(ulong FirstSector, ulong SectorCount, ulong DataStart,
                                   List<BlockEntry> Blocks);

  internal static MishTable? ParseMish(byte[] mish) {
    if (mish.Length < 204 || !mish.AsSpan(0, 4).SequenceEqual("mish"u8)) return null;

    var firstSector = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(8));
    var sectorCount = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(16));
    var dataStart = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(24));
    var numEntries = BinaryPrimitives.ReadUInt32BigEndian(mish.AsSpan(200));
    if (numEntries > 100_000) return null;

    var blocks = new List<BlockEntry>((int)numEntries);
    var off = 204;
    for (var i = 0u; i < numEntries; i++) {
      if (off + 40 > mish.Length) break;
      blocks.Add(new BlockEntry(
        BinaryPrimitives.ReadUInt32BigEndian(mish.AsSpan(off)),
        BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(off + 8)),
        BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(off + 16)),
        BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(off + 24)),
        BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(off + 32))));
      off += 40;
    }
    return new MishTable(firstSector, sectorCount, dataStart, blocks);
  }

  private static long ComputePartitionSize(byte[] mish) {
    var table = ParseMish(mish);
    return table == null ? 0 : checked((long)table.SectorCount * SectorSize);
  }

  /// <summary>Reassembles and returns the raw sector data for <paramref name="entry"/>.</summary>
  public byte[] Extract(DmgEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var pi = _partitions.FirstOrDefault(p => p.Name == entry.Name);
    if (pi == null) return [];

    var table = ParseMish(pi.Mish);
    if (table == null) return [];

    var physicalBytes = checked((long)table.SectorCount * SectorSize);
    if (physicalBytes < 0 || physicalBytes > int.MaxValue)
      throw new NotSupportedException("DMG partition is too large for the in-memory extraction API.");

    var output = new byte[(int)physicalBytes];
    foreach (var block in table.Blocks) {
      if (block.Type == BlockTypeComment || block.Type == BlockTypeTerminator) continue;
      var destOffset = checked((long)block.SectorOffset * SectorSize);
      var destLength = checked((long)block.SectorCount * SectorSize);
      if (destLength == 0 || destOffset < 0 || destOffset + destLength > output.LongLength) continue;

      switch (block.Type) {
        case BlockTypeZeroFill: break;
        case BlockTypeRaw: ExtractRaw(block, destOffset, destLength, output); break;
        case BlockTypeZlib: ExtractZlib(block, destOffset, destLength, output); break;
        case BlockTypeBzip2: ExtractBzip2(block, destOffset, destLength, output); break;
        case BlockTypeLzfse:
        case BlockTypeLzma:
        default:
          break;
      }
    }

    if (pi.LogicalSize == output.LongLength) return output;
    return output.AsSpan(0, checked((int)Math.Min(pi.LogicalSize, output.LongLength))).ToArray();
  }

  private void ExtractRaw(BlockEntry block, long destOffset, long destLength, byte[] output) {
    var srcOffset = checked((long)block.CompressedOffset);
    var srcLength = checked((long)block.CompressedLength);
    if (srcOffset < 0 || srcOffset + srcLength > _data.LongLength) return;
    var copyLen = checked((int)Math.Min(srcLength, destLength));
    _data.AsSpan(checked((int)srcOffset), copyLen).CopyTo(output.AsSpan(checked((int)destOffset)));
  }

  private void ExtractZlib(BlockEntry block, long destOffset, long destLength, byte[] output) {
    var srcOffset = checked((long)block.CompressedOffset);
    var srcLength = checked((long)block.CompressedLength);
    if (srcOffset < 0 || srcLength < 2 || srcOffset + srcLength > _data.LongLength) return;
    try {
      using var src = new MemoryStream(_data, checked((int)srcOffset + 2), checked((int)srcLength - 2));
      using var deflate = new DeflateStream(src, CompressionMode.Decompress);
      using var dst = new MemoryStream(output, checked((int)destOffset), checked((int)destLength));
      deflate.CopyTo(dst);
    } catch {
      // Corrupt/unsupported block: keep the destination zero-filled.
    }
  }

  private static void ExtractBzip2(BlockEntry block, long destOffset, long destLength, byte[] output) {
    _ = block; _ = destOffset; _ = destLength; _ = output;
  }

  public void Dispose() { }

  internal sealed record PartitionInfo(string Name, byte[] Mish, long LogicalSize);
}
