#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using Compression.Core.Streams;
using FileFormat.Bzip2;
using FileFormat.Lzfse;
using FileFormat.Lzma;

namespace FileFormat.Dmg;

/// <summary>
/// Reader for Apple Disk Image (DMG/UDIF) files. Parses the koly trailer,
/// XML plist, and mish block tables to expose each partition as an entry.
/// </summary>
public sealed class DmgReader : IDisposable {
  internal const uint BlockTypeZeroFill   = 0x00000000;
  internal const uint BlockTypeRaw        = 0x00000001;
  internal const uint BlockTypeIgnore     = 0x00000002;
  internal const uint BlockTypeAdc        = 0x80000004;
  internal const uint BlockTypeZlib       = 0x80000005;
  internal const uint BlockTypeBzip2      = 0x80000006;
  internal const uint BlockTypeLzfse      = 0x80000007;
  internal const uint BlockTypeLzma       = 0x80000008;
  internal const uint BlockTypeComment    = 0x7FFFFFFE;
  internal const uint BlockTypeTerminator = 0xFFFFFFFF;

  private const int KolySize = 512;
  private const int SectorSize = 512;

  private readonly byte[] _data;
  private readonly List<DmgEntry> _entries = [];
  private readonly List<PartitionInfo> _partitions = [];

  public IReadOnlyList<DmgEntry> Entries => _entries;
  internal long DataForkOffset { get; private set; }
  internal long DataForkLength { get; private set; }
  internal long XmlOffset { get; private set; }
  internal long XmlLength { get; private set; }
  internal long FileLength => _data.LongLength;
  internal byte[] KolyTrailer { get; private set; } = [];
  internal IReadOnlyList<PartitionInfo> Partitions => _partitions;
  internal bool IsWorkbenchRawProfile =>
    DataForkOffset == 0 && DataForkLength == XmlOffset &&
    _partitions.All(p => p.HasLogicalSizeMarker && IsRawMish(p.Mish));

  /// <summary>
  /// Initializes a new instance of <see cref="DmgReader"/>.
  /// </summary>
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

    DataForkOffset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(kolySpan[24..]));
    DataForkLength = checked((long)BinaryPrimitives.ReadUInt64BigEndian(kolySpan[32..]));
    XmlOffset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(kolySpan[216..]));
    XmlLength = checked((long)BinaryPrimitives.ReadUInt64BigEndian(kolySpan[224..]));
    KolyTrailer = kolySpan.ToArray();

    if (DataForkOffset < 0 || DataForkLength < 0 ||
        checked(DataForkOffset + DataForkLength) > kolyOff)
      throw new InvalidDataException("DMG: invalid data-fork region in koly trailer.");
    if (XmlLength <= 0 || XmlOffset < 0 || checked(XmlOffset + XmlLength) > kolyOff)
      throw new InvalidDataException("DMG: invalid XML plist region in koly trailer.");

    var xmlText = Encoding.UTF8.GetString(_data, checked((int)XmlOffset), checked((int)XmlLength));
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
        var logicalSize = parsed.LogicalSize.HasValue
          ? Math.Min(parsed.LogicalSize.Value, physicalSize)
          : physicalSize;
        _entries.Add(new DmgEntry { Name = parsed.Name, Size = logicalSize });
        _partitions.Add(new PartitionInfo(parsed.Name, parsed.Mish, logicalSize, parsed.LogicalSize.HasValue));
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

    var version = BinaryPrimitives.ReadUInt32BigEndian(mish.AsSpan(4));
    if (version != 1)
      throw new NotSupportedException($"DMG: unsupported mish version {version}.");

    var firstSector = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(8));
    var sectorCount = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(16));
    var dataStart = BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(24));
    var numEntries = BinaryPrimitives.ReadUInt32BigEndian(mish.AsSpan(200));
    if (numEntries > 100_000)
      throw new InvalidDataException("DMG: unreasonable mish block-entry count.");

    var requiredLength = checked(204L + (long)numEntries * 40);
    if (requiredLength > mish.LongLength)
      throw new InvalidDataException("DMG: truncated mish block table.");

    var blocks = new List<BlockEntry>((int)numEntries);
    var off = 204;
    for (var i = 0u; i < numEntries; i++) {
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

  internal static bool IsRawMish(byte[] mish) {
    var table = ParseMish(mish);
    return table != null && table.Blocks.All(b => b.Type is BlockTypeRaw or BlockTypeTerminator);
  }

  private static long ComputePartitionSize(byte[] mish) {
    var table = ParseMish(mish);
    return table == null ? 0 : checked((long)table.SectorCount * SectorSize);
  }

  public byte[] Extract(DmgEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var pi = _partitions.FirstOrDefault(p => p.Name == entry.Name);
    if (pi == null) return [];

    var table = ParseMish(pi.Mish);
    if (table == null) return [];

    var physicalBytes = checked((long)table.SectorCount * SectorSize);
    if (physicalBytes > int.MaxValue)
      throw new NotSupportedException("DMG partition is too large for the in-memory extraction API.");

    var output = new byte[(int)physicalBytes];
    foreach (var block in table.Blocks) {
      if (block.Type is BlockTypeComment or BlockTypeTerminator) continue;

      var destOffset = checked((long)block.SectorOffset * SectorSize);
      var destLength = checked((long)block.SectorCount * SectorSize);
      if (destLength == 0) continue;
      if (destOffset < 0 || checked(destOffset + destLength) > output.LongLength)
        throw new InvalidDataException("DMG: mish chunk points outside its partition sector range.");

      switch (block.Type) {
        case BlockTypeZeroFill:
        case BlockTypeIgnore:
          break;
        case BlockTypeRaw:
          ExtractRaw(block, destOffset, destLength, output);
          break;
        case BlockTypeZlib:
          ExtractStream(block, destOffset, destLength, output, "zlib",
            static source => new ZLibStream(source, CompressionMode.Decompress, leaveOpen: false));
          break;
        case BlockTypeBzip2:
          ExtractStream(block, destOffset, destLength, output, "bzip2",
            static source => new Bzip2Stream(source, CompressionStreamMode.Decompress, leaveOpen: false));
          break;
        case BlockTypeLzfse:
          ExtractBuffered(block, destOffset, destLength, output, "LZFSE", LzfseStream.Decompress);
          break;
        case BlockTypeLzma:
          ExtractBuffered(block, destOffset, destLength, output, "LZMA", LzmaStream.Decompress);
          break;
        case BlockTypeAdc:
          ExtractAdc(block, destOffset, destLength, output);
          break;
        default:
          throw new NotSupportedException($"DMG: unsupported mish chunk type 0x{block.Type:X8}.");
      }
    }

    if (pi.LogicalSize == output.LongLength) return output;
    return output.AsSpan(0, checked((int)Math.Min(pi.LogicalSize, output.LongLength))).ToArray();
  }

  private void ExtractRaw(BlockEntry block, long destOffset, long destLength, byte[] output) {
    var (srcOffset, srcLength) = ResolveSourceRange(block);
    if (srcLength != destLength)
      throw new InvalidDataException(
        $"DMG: raw chunk stores {srcLength} bytes for a {destLength}-byte sector range.");
    _data.AsSpan(srcOffset, srcLength).CopyTo(output.AsSpan(checked((int)destOffset), checked((int)destLength)));
  }

  private void ExtractAdc(BlockEntry block, long destOffset, long destLength, byte[] output) {
    var (srcOffset, srcLength) = ResolveSourceRange(block);
    try {
      DmgAdcDecoder.Decode(_data.AsSpan(srcOffset, srcLength),
        output.AsSpan(checked((int)destOffset), checked((int)destLength)));
    } catch (Exception ex) when (ex is InvalidDataException or OverflowException) {
      throw new InvalidDataException($"DMG: corrupt ADC chunk at data-fork offset {block.CompressedOffset}.", ex);
    }
  }

  private void ExtractStream(BlockEntry block, long destOffset, long destLength, byte[] output,
      string codec, Func<Stream, Stream> decoderFactory) {
    var (srcOffset, srcLength) = ResolveSourceRange(block);
    try {
      using var source = new MemoryStream(_data, srcOffset, srcLength, writable: false);
      using var decoder = decoderFactory(source);
      ReadDecodedExactly(decoder, output.AsSpan(checked((int)destOffset), checked((int)destLength)), codec);
    } catch (NotSupportedException) {
      throw;
    } catch (Exception ex) when (ex is not OutOfMemoryException) {
      throw new InvalidDataException($"DMG: corrupt {codec} chunk at data-fork offset {block.CompressedOffset}.", ex);
    }
  }

  private void ExtractBuffered(BlockEntry block, long destOffset, long destLength, byte[] output,
      string codec, Action<Stream, Stream> decoder) {
    var (srcOffset, srcLength) = ResolveSourceRange(block);
    try {
      using var source = new MemoryStream(_data, srcOffset, srcLength, writable: false);
      using var decoded = new MemoryStream(checked((int)destLength));
      decoder(source, decoded);
      if (decoded.Length != destLength)
        throw new InvalidDataException(
          $"DMG: {codec} chunk decoded {decoded.Length} bytes, expected {destLength}.");
      decoded.GetBuffer().AsSpan(0, checked((int)destLength))
        .CopyTo(output.AsSpan(checked((int)destOffset), checked((int)destLength)));
    } catch (NotSupportedException) {
      throw;
    } catch (Exception ex) when (ex is not OutOfMemoryException) {
      throw new InvalidDataException($"DMG: corrupt {codec} chunk at data-fork offset {block.CompressedOffset}.", ex);
    }
  }

  private (int Offset, int Length) ResolveSourceRange(BlockEntry block) {
    long offset;
    long length;
    try {
      offset = checked(DataForkOffset + (long)block.CompressedOffset);
      length = checked((long)block.CompressedLength);
    } catch (OverflowException ex) {
      throw new InvalidDataException("DMG: chunk source range overflows the address space.", ex);
    }

    var dataForkEnd = checked(DataForkOffset + DataForkLength);
    if (offset < DataForkOffset || length < 0 || checked(offset + length) > dataForkEnd ||
        checked(offset + length) > _data.LongLength)
      throw new InvalidDataException("DMG: mish chunk points outside the UDIF data fork.");

    try {
      return (checked((int)offset), checked((int)length));
    } catch (OverflowException ex) {
      throw new NotSupportedException("DMG chunk is too large for the in-memory reader.", ex);
    }
  }

  internal (long Offset, long Length) GetStoredRange(BlockEntry block) {
    var (offset, length) = ResolveSourceRange(block);
    return (offset, length);
  }

  private static void ReadDecodedExactly(Stream decoder, Span<byte> destination, string codec) {
    var done = 0;
    while (done < destination.Length) {
      var read = decoder.Read(destination[done..]);
      if (read == 0)
        throw new InvalidDataException(
          $"DMG: truncated {codec} chunk; decoded {done} of {destination.Length} bytes.");
      done += read;
    }

    Span<byte> extra = stackalloc byte[1];
    if (decoder.Read(extra) != 0)
      throw new InvalidDataException(
        $"DMG: {codec} chunk expands beyond its declared sector range.");
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }

  internal sealed record PartitionInfo(string Name, byte[] Mish, long LogicalSize, bool HasLogicalSizeMarker);
}
