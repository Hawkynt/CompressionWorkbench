#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Sqlite;

/// <summary>
/// SQLite 3 database read-only surfacing descriptor.
/// Emits the raw pages, the 100-byte database header, a metadata.ini summary,
/// and a textual dump of the freelist trunk chain. Does NOT decode B-trees or
/// execute SQL: pure page-level introspection.
/// </summary>
public sealed class SqliteFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SQLite format 3\0");
  private const int HeaderSize = 100;

  public string Id => "Sqlite";
  public string DisplayName => "SQLite 3 Database";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sqlite";
  public IReadOnlyList<string> Extensions => [".sqlite", ".sqlite3", ".db3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(Magic, Offset: 0, Confidence: 0.98)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "SQLite 3 database (page-level surfacing only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var streamLen = stream.Length;
    var entries = new List<ArchiveEntryInfo>();
    int idx = 0;

    entries.Add(new ArchiveEntryInfo(idx++, "FULL.sqlite", streamLen, streamLen, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "Stored", false, false, null));

    var header = ReadHeader(stream);
    if (!TryParseHeader(header, streamLen, out var info)) {
      entries.Add(new ArchiveEntryInfo(idx++, "page_01_header.bin", 0, 0, "Stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(idx++, "page_01_header.bin", HeaderSize, HeaderSize, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "freelist_trunks.txt", 0, 0, "Stored", false, false, null));

    for (int p = 2; p <= info.PageCount; p++) {
      var name = $"pages/page_{p:D4}.bin";
      entries.Add(new ArchiveEntryInfo(idx++, name, info.PageSize, info.PageSize, "Stored", false, false, null));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var streamLen = stream.Length;

    // Stream FULL.sqlite directly — never buffer the whole file.
    if (files == null || !HasFilters(files) || MatchesFilter("FULL.sqlite", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.sqlite");
      var d = Path.GetDirectoryName(fullPath);
      if (d != null) Directory.CreateDirectory(d);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }

    void Emit(string name, byte[] data) {
      if (files != null && HasFilters(files) && !MatchesFilter(name, files)) return;
      WriteFile(outputDir, name, data);
    }

    var header = ReadHeader(stream);
    if (!TryParseHeader(header, streamLen, out var info)) {
      Emit("metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(streamLen, info, parseStatus: "partial")));
      return;
    }

    Emit("metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(streamLen, info, parseStatus: "ok")));
    Emit("page_01_header.bin", header);
    Emit("freelist_trunks.txt", Encoding.UTF8.GetBytes(DumpFreelistTrunks(stream, info, streamLen)));

    // Per-page extraction via seek+read. Each page buffer is <= 64 KB — bounded.
    var pageBuf = new byte[info.PageSize];
    for (int p = 2; p <= info.PageCount; p++) {
      long offset = (long)(p - 1) * info.PageSize;
      if (offset + info.PageSize > streamLen) break;
      var name = $"pages/page_{p:D4}.bin";
      if (files != null && HasFilters(files) && !MatchesFilter(name, files)) continue;
      stream.Seek(offset, SeekOrigin.Begin);
      stream.ReadExactly(pageBuf, 0, info.PageSize);
      // Clone since WriteFile takes a byte[] — the buffer is reused each iteration.
      var copy = new byte[info.PageSize];
      Buffer.BlockCopy(pageBuf, 0, copy, 0, info.PageSize);
      WriteFile(outputDir, name, copy);
    }
  }

  private static bool HasFilters(string[]? files) => files != null && files.Length > 0;

  // Reads the first 100 header bytes (or fewer, if the stream is shorter).
  private static byte[] ReadHeader(Stream stream) {
    stream.Seek(0, SeekOrigin.Begin);
    var len = (int)Math.Min(HeaderSize, stream.Length);
    var buf = new byte[len];
    var read = 0;
    while (read < len) {
      var n = stream.Read(buf, read, len - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  private static bool TryParseHeader(byte[] header, long streamLen, out SqliteInfo info) {
    info = default;
    if (header.Length < 100) return false;
    for (int i = 0; i < Magic.Length; i++)
      if (header[i] != Magic[i]) return false;

    int pageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16, 2));
    int actualPageSize = pageSize == 1 ? 65536 : pageSize;
    if (actualPageSize < 512 || actualPageSize > 65536) return false;
    if ((actualPageSize & (actualPageSize - 1)) != 0) return false;

    uint dbSizePages = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28, 4));
    uint schemaCookie = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(40, 4));
    uint textEncoding = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(56, 4));
    uint userVersion = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(60, 4));
    uint applicationId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(68, 4));
    uint sqliteVersion = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(96, 4));
    uint firstFreelist = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32, 4));
    uint totalFreelist = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(36, 4));

    long totalPagesOnDisk = streamLen / actualPageSize;
    int pageCount = dbSizePages > 0 && dbSizePages <= totalPagesOnDisk
      ? (int)dbSizePages
      : (int)Math.Min(totalPagesOnDisk, int.MaxValue);

    info = new SqliteInfo {
      PageSize = actualPageSize,
      PageSizeRaw = pageSize,
      PageCount = pageCount,
      DbSizePagesField = dbSizePages,
      SchemaCookie = schemaCookie,
      TextEncoding = textEncoding,
      UserVersion = userVersion,
      ApplicationId = applicationId,
      SqliteVersion = sqliteVersion,
      FirstFreelistTrunk = firstFreelist,
      TotalFreelistPages = totalFreelist,
      TotalPagesInFile = (int)Math.Min(totalPagesOnDisk, int.MaxValue),
    };
    return true;
  }

  private static string BuildMetadataIni(long fileSize, SqliteInfo info, string parseStatus) {
    var sb = new StringBuilder();
    sb.AppendLine("[sqlite]");
    sb.AppendLine($"parse_status={parseStatus}");
    sb.AppendLine($"file_size={fileSize}");
    if (parseStatus == "ok") {
      sb.AppendLine($"page_size={info.PageSize}");
      sb.AppendLine($"page_size_raw={info.PageSizeRaw}");
      sb.AppendLine($"database_size_pages={info.DbSizePagesField}");
      sb.AppendLine($"total_pages_in_file={info.TotalPagesInFile}");
      sb.AppendLine($"text_encoding={TextEncodingName(info.TextEncoding)}");
      sb.AppendLine($"sqlite_version={info.SqliteVersion}");
      sb.AppendLine($"schema_cookie={info.SchemaCookie}");
      sb.AppendLine($"user_version={info.UserVersion}");
      sb.AppendLine($"application_id=0x{info.ApplicationId:X8}");
      sb.AppendLine($"first_freelist_trunk={info.FirstFreelistTrunk}");
      sb.AppendLine($"total_freelist_pages={info.TotalFreelistPages}");
    }
    return sb.ToString();
  }

  private static string TextEncodingName(uint v) => v switch {
    1 => "utf-8",
    2 => "utf-16le",
    3 => "utf-16be",
    _ => $"unknown({v})",
  };

  // Walks the freelist trunk chain via seek+read. Uses long arithmetic throughout
  // to stay correct for databases > 2 GB.
  private static string DumpFreelistTrunks(Stream stream, SqliteInfo info, long streamLen) {
    var sb = new StringBuilder();
    sb.AppendLine("# trunk_page\tnext_trunk\tleaf_count");
    uint trunk = info.FirstFreelistTrunk;
    var seen = new HashSet<uint>();
    Span<byte> head = stackalloc byte[8];
    while (trunk != 0 && !seen.Contains(trunk) && trunk <= info.PageCount) {
      seen.Add(trunk);
      long off = (long)(trunk - 1) * info.PageSize;
      if (off + 8 > streamLen) break;
      stream.Seek(off, SeekOrigin.Begin);
      stream.ReadExactly(head);
      uint next = BinaryPrimitives.ReadUInt32BigEndian(head[..4]);
      uint leafCount = BinaryPrimitives.ReadUInt32BigEndian(head[4..8]);
      sb.Append(trunk.ToString(CultureInfo.InvariantCulture));
      sb.Append('\t');
      sb.Append(next.ToString(CultureInfo.InvariantCulture));
      sb.Append('\t');
      sb.Append(leafCount.ToString(CultureInfo.InvariantCulture));
      sb.AppendLine();
      trunk = next;
    }
    return sb.ToString();
  }

  private struct SqliteInfo {
    public int PageSize;
    public int PageSizeRaw;
    public int PageCount;
    public uint DbSizePagesField;
    public uint SchemaCookie;
    public uint TextEncoding;
    public uint UserVersion;
    public uint ApplicationId;
    public uint SqliteVersion;
    public uint FirstFreelistTrunk;
    public uint TotalFreelistPages;
    public int TotalPagesInFile;
  }
}
