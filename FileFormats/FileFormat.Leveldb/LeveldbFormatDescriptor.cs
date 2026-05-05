#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Leveldb;

/// <summary>
/// LevelDB SSTable (.ldb / .sst) read-only surfacing descriptor. Parses the
/// 48-byte fixed footer (two BlockHandles + magic) and emits the footer, a
/// metadata.ini, and the data region preceding the metaindex as a single
/// blob (block-level splitting requires parsing restart arrays which is out
/// of scope for this page-level surface).
/// </summary>
public sealed class LeveldbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  // Canonical LevelDB magic (little-endian uint64 0xdb4775248b80fb57) as raw bytes.
  private static readonly byte[] Magic = [0x57, 0xFB, 0x80, 0x8B, 0x24, 0x75, 0x47, 0xDB];
  private const int FooterSize = 48;
  private const int CopyBufferSize = 64 * 1024;

  public string Id => "Leveldb";
  public string DisplayName => "LevelDB SSTable";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ldb";
  public IReadOnlyList<string> Extensions => [".ldb", ".sst"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Magic lives in the last 8 bytes; the detector only scans a header prefix,
  // so detection happens via .ldb/.sst extensions. The magic is still verified
  // inside TryParseFooter and reported in metadata.ini as magic_ok.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "LevelDB / RocksDB SSTable (.ldb/.sst) with footer surfacing";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var streamLen = stream.Length;
    var entries = new List<ArchiveEntryInfo>();
    int idx = 0;

    entries.Add(new ArchiveEntryInfo(idx++, "FULL.ldb", streamLen, streamLen, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "Stored", false, false, null));

    if (streamLen < FooterSize) return entries;

    var footer = ReadFooter(stream, streamLen);
    entries.Add(new ArchiveEntryInfo(idx++, "footer.bin", FooterSize, FooterSize, "Stored", false, false, null));

    if (TryParseFooter(footer, out var f)) {
      long dataLen = Math.Max(0, f.MetaindexOffset);
      entries.Add(new ArchiveEntryInfo(idx++, "data_blocks.bin", dataLen, dataLen, "Stored", false, false, null));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var streamLen = stream.Length;

    // Stream FULL.ldb directly — never buffer the whole file.
    if (files == null || files.Length == 0 || MatchesFilter("FULL.ldb", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.ldb");
      var d = Path.GetDirectoryName(fullPath);
      if (d != null) Directory.CreateDirectory(d);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }

    byte[] footer = streamLen >= FooterSize ? ReadFooter(stream, streamLen) : [];
    Footer f = default;
    var parsed = footer.Length == FooterSize && TryParseFooter(footer, out f);

    void EmitBytes(string name, byte[] data) {
      if (files != null && files.Length > 0 && !MatchesFilter(name, files)) return;
      WriteFile(outputDir, name, data);
    }

    EmitBytes("metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(streamLen, f, parsed)));

    if (footer.Length == FooterSize)
      EmitBytes("footer.bin", footer);

    if (parsed && f.MetaindexOffset >= 0 && f.MetaindexOffset <= streamLen) {
      if (files == null || files.Length == 0 || MatchesFilter("data_blocks.bin", files))
        StreamCopyRange(stream, outputDir, "data_blocks.bin", 0, f.MetaindexOffset);
    }
  }

  private static byte[] ReadFooter(Stream stream, long streamLen) {
    var buf = new byte[FooterSize];
    stream.Seek(streamLen - FooterSize, SeekOrigin.Begin);
    stream.ReadExactly(buf, 0, FooterSize);
    return buf;
  }

  // Copies a byte range from the stream directly into a target file using a bounded 64 KB buffer.
  private static void StreamCopyRange(Stream stream, string outputDir, string entryName, long offset, long length) {
    var safeName = entryName.Replace('\\', '/').TrimStart('/');
    if (safeName.Contains("..")) safeName = Path.GetFileName(safeName);
    var fullPath = Path.Combine(outputDir, safeName);
    var dir = Path.GetDirectoryName(fullPath);
    if (dir != null) Directory.CreateDirectory(dir);

    using var outStream = File.Create(fullPath);
    if (length <= 0) return;

    stream.Seek(offset, SeekOrigin.Begin);
    var buf = new byte[(int)Math.Min(CopyBufferSize, length)];
    var remaining = length;
    while (remaining > 0) {
      var toRead = (int)Math.Min(buf.Length, remaining);
      var n = stream.Read(buf, 0, toRead);
      if (n <= 0) break;
      outStream.Write(buf, 0, n);
      remaining -= n;
    }
  }

  private static bool TryParseFooter(byte[] footer, out Footer parsed) {
    parsed = default;
    if (footer.Length < FooterSize) return false;

    // Magic in the last 8 bytes.
    for (int i = 0; i < 8; i++)
      if (footer[FooterSize - 8 + i] != Magic[i]) return false;

    // BlockHandles (metaindex then index) are varint-encoded into the first
    // 40 bytes of the footer (padded with trailing zeros).
    int pos = 0;
    int end = 40;

    if (!TryReadVarint64(footer, ref pos, end, out var metaOff)) return false;
    if (!TryReadVarint64(footer, ref pos, end, out var metaSize)) return false;
    if (!TryReadVarint64(footer, ref pos, end, out var idxOff)) return false;
    if (!TryReadVarint64(footer, ref pos, end, out var idxSize)) return false;

    parsed = new Footer {
      MagicOk = true,
      MetaindexOffset = (long)metaOff,
      MetaindexSize = (long)metaSize,
      IndexOffset = (long)idxOff,
      IndexSize = (long)idxSize,
    };
    return true;
  }

  private static bool TryReadVarint64(byte[] data, ref int pos, int end, out ulong value) {
    value = 0;
    int shift = 0;
    while (pos < end && shift <= 63) {
      byte b = data[pos++];
      value |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0) return true;
      shift += 7;
    }
    return false;
  }

  private static string BuildMetadataIni(long fileSize, Footer f, bool parsed) {
    var sb = new StringBuilder();
    sb.AppendLine("[leveldb]");
    sb.AppendLine($"parse_status={(parsed ? "ok" : "partial")}");
    sb.AppendLine($"file_size={fileSize}");
    sb.AppendLine($"magic_ok={(parsed && f.MagicOk ? "true" : "false")}");
    if (parsed) {
      sb.AppendLine($"metaindex_offset={f.MetaindexOffset}");
      sb.AppendLine($"metaindex_size={f.MetaindexSize}");
      sb.AppendLine($"index_offset={f.IndexOffset}");
      sb.AppendLine($"index_size={f.IndexSize}");
    }
    return sb.ToString();
  }

  private struct Footer {
    public bool MagicOk;
    public long MetaindexOffset;
    public long MetaindexSize;
    public long IndexOffset;
    public long IndexSize;
  }
}
