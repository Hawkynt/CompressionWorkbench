#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cso;

/// <summary>
/// PSP CSO / ZSO compressed ISO image. Layout after the 4-byte magic (<c>CISO</c> for CSO, <c>ZISO</c>
/// for LZ4-compressed ZSO): uint32 header_size, uint64 uncompressed_size, uint32 block_size,
/// uint8 version, uint8 align, uint16 reserved, then an index table of <c>N = uncompressed_size /
/// block_size + 1</c> uint32 entries (high bit = stored/uncompressed, low 31 bits = file offset).
///
/// <para>This descriptor surfaces each compressed block as a raw blob — it does NOT decompress
/// the blocks (consumers can further process with zlib for CSO or LZ4 for ZSO).</para>
/// </summary>
public sealed class CsoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Cso";
  public string DisplayName => "PSP CSO/ZSO";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cso";
  public IReadOnlyList<string> Extensions => [".cso", ".ziso", ".zso"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("CISO"u8.ToArray(), Confidence: 0.90),
    new("ZISO"u8.ToArray(), Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("deflate", "Deflate"),
    new("lz4", "LZ4"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PSP CSO (zlib) / ZSO (LZ4) compressed ISO image.";

  private const uint IndexUncompressedMask = 0x8000_0000u;
  private const uint IndexOffsetMask = 0x7FFF_FFFFu;

  private sealed record CsoLayout(
    long FullSize,
    bool IsZso,
    string Magic,
    uint HeaderSize,
    ulong UncompressedSize,
    uint BlockSize,
    byte Version,
    byte Align,
    int BlockCount,
    uint[] IndexRaw
  );

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var layout = ReadLayout(stream);
    var entries = new List<ArchiveEntryInfo>(4 + layout.BlockCount);
    var ext = layout.IsZso ? "ziso" : "cso";
    entries.Add(new ArchiveEntryInfo(0, $"FULL.{ext}", layout.FullSize, layout.FullSize, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(2, "index.bin", layout.IndexRaw.Length * 4L, layout.IndexRaw.Length * 4L, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(3, "blocks", 0, 0, "Stored", true, false, null));

    var storedMethod = layout.IsZso ? "LZ4" : "Deflate";
    for (var i = 0; i < layout.BlockCount; ++i) {
      var (offset, size, isUncompressed) = GetBlockSpan(layout, i);
      var method = isUncompressed ? "Stored" : storedMethod;
      entries.Add(new ArchiveEntryInfo(
        Index: 4 + i,
        Name: $"blocks/block_{i:D5}.bin",
        OriginalSize: size,
        CompressedSize: size,
        Method: method,
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: null,
        Kind: isUncompressed ? "stored" : "compressed"));
      // offset intentionally referenced to avoid unused-local warning in contexts that strip.
      _ = offset;
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var layout = ReadLayout(stream);
    var ext = layout.IsZso ? "ziso" : "cso";
    var fullName = $"FULL.{ext}";

    if (Wants(files, fullName)) {
      stream.Position = 0;
      var buf = new byte[layout.FullSize];
      ReadExact(stream, buf);
      WriteFile(outputDir, fullName, buf);
    }

    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(layout)));

    if (Wants(files, "index.bin")) {
      var idxBytes = new byte[layout.IndexRaw.Length * 4];
      for (var i = 0; i < layout.IndexRaw.Length; ++i)
        BinaryPrimitives.WriteUInt32LittleEndian(idxBytes.AsSpan(i * 4, 4), layout.IndexRaw[i]);
      WriteFile(outputDir, "index.bin", idxBytes);
    }

    for (var i = 0; i < layout.BlockCount; ++i) {
      var name = $"blocks/block_{i:D5}.bin";
      if (!Wants(files, name)) continue;
      var (offset, size, _) = GetBlockSpan(layout, i);
      var data = ReadRange(stream, offset, size);
      WriteFile(outputDir, name, data);
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static CsoLayout ReadLayout(Stream stream) {
    if (!stream.CanSeek)
      throw new InvalidDataException("CSO/ZSO descriptor requires a seekable stream.");
    stream.Position = 0;
    var fullSize = stream.Length;

    Span<byte> header = stackalloc byte[24];
    ReadExact(stream, header);
    var magic = Encoding.ASCII.GetString(header[..4]);
    var isZso = magic switch {
      "CISO" => false,
      "ZISO" => true,
      _ => throw new InvalidDataException("Not a CSO/ZSO image (missing CISO/ZISO magic)."),
    };

    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
    var uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(header[8..16]);
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
    var version = header[20];
    var align = header[21];
    // header[22..24] reserved.

    if (blockSize == 0)
      throw new InvalidDataException("CSO/ZSO block_size is zero.");
    // Block count is uncompressed_size / block_size + 1 index entries form the table, last entry
    // marks end of file. Number of actual blocks is uncompressed_size / block_size (ceil).
    var blockCountLong = (long)((uncompressedSize + blockSize - 1) / blockSize);
    if (blockCountLong < 0 || blockCountLong > 8_000_000)
      throw new InvalidDataException($"CSO/ZSO block count implausible: {blockCountLong}.");
    var blockCount = (int)blockCountLong;

    var indexCount = blockCount + 1;
    var indexBytes = new byte[indexCount * 4];
    // The index table immediately follows the 24-byte header (header_size is typically 0x18=24).
    stream.Position = 24;
    ReadExact(stream, indexBytes);
    var indexRaw = new uint[indexCount];
    for (var i = 0; i < indexCount; ++i)
      indexRaw[i] = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.AsSpan(i * 4, 4));

    return new CsoLayout(
      FullSize: fullSize,
      IsZso: isZso,
      Magic: magic,
      HeaderSize: headerSize,
      UncompressedSize: uncompressedSize,
      BlockSize: blockSize,
      Version: version,
      Align: align,
      BlockCount: blockCount,
      IndexRaw: indexRaw);
  }

  private static (long Offset, long Size, bool IsUncompressed) GetBlockSpan(CsoLayout layout, int blockIndex) {
    var raw = layout.IndexRaw[blockIndex];
    var nextRaw = layout.IndexRaw[blockIndex + 1];
    var isUncompressed = (raw & IndexUncompressedMask) != 0;
    var offset = (long)(raw & IndexOffsetMask) << layout.Align;
    var nextOffset = (long)(nextRaw & IndexOffsetMask) << layout.Align;
    var size = Math.Max(0, nextOffset - offset);
    if (offset < 0 || offset > layout.FullSize)
      throw new InvalidDataException($"CSO/ZSO block {blockIndex} offset out of range.");
    if (offset + size > layout.FullSize)
      size = Math.Max(0, layout.FullSize - offset);
    return (offset, size, isUncompressed);
  }

  private static string BuildMetadataIni(CsoLayout layout) {
    var sb = new StringBuilder();
    sb.Append("[Cso]\n");
    sb.Append(CultureInfo.InvariantCulture, $"magic={layout.Magic}\n");
    sb.Append(CultureInfo.InvariantCulture, $"is_zso={(layout.IsZso ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_size={layout.HeaderSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"uncompressed_size={layout.UncompressedSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"block_size={layout.BlockSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={layout.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"align={layout.Align}\n");
    sb.Append(CultureInfo.InvariantCulture, $"block_count={layout.BlockCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_size={layout.FullSize}\n");
    return sb.ToString();
  }

  private static byte[] ReadRange(Stream stream, long offset, long size) {
    if (size <= 0) return [];
    if (size > int.MaxValue)
      throw new InvalidDataException("CSO/ZSO block too large to extract.");
    stream.Position = offset;
    var buf = new byte[(int)size];
    ReadExact(stream, buf);
    return buf;
  }

  private static void ReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n <= 0) throw new EndOfStreamException("Unexpected end of CSO/ZSO stream.");
      read += n;
    }
  }
}
