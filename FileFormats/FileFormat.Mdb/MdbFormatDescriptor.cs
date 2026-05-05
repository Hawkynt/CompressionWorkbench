#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mdb;

/// <summary>
/// Microsoft Access Jet Red / ACCDB read-only surfacing descriptor.
/// Emits raw pages, the first-page header, metadata.ini, and (if locatable) the
/// MSysObjects root-page pointer. Does NOT decode Jet B-trees or rows.
/// </summary>
public sealed class MdbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private static readonly byte[] JetSignature = Encoding.ASCII.GetBytes("Standard Jet DB");
  private static readonly byte[] AceSignature = Encoding.ASCII.GetBytes("Standard ACE DB");
  // Maximum bytes we read from the head to detect variant + MSysObjects pointer.
  // The version byte is at 0x14 and the pointer at 0x2C — 64 bytes is plenty.
  private const int HeadPeekSize = 64;

  public string Id => "Mdb";
  public string DisplayName => "Microsoft Access (Jet Red / ACCDB)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mdb";
  public IReadOnlyList<string> Extensions => [".mdb", ".accdb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(JetSignature, Offset: 4, Confidence: 0.95),
    new(AceSignature, Offset: 4, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft Access database (Jet Red 3/4, ACCDB) page-level surfacing";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var streamLen = stream.Length;
    var head = ReadHead(stream);
    var variant = FormatVariant(head);
    var entries = new List<ArchiveEntryInfo>();
    int idx = 0;

    var ext = variant switch {
      Variant.Jet3 => "mdb",
      Variant.Jet4 => "mdb",
      Variant.AccDb => "accdb",
      _ => "mdb",
    };
    entries.Add(new ArchiveEntryInfo(idx++, $"FULL.{ext}", streamLen, streamLen, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "Stored", false, false, null));

    if (!TryBuildInfo(variant, streamLen, out var info)) return entries;

    entries.Add(new ArchiveEntryInfo(idx++, "page_00_header.bin", info.PageSize, info.PageSize, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "msysobjects_pointer.txt", 0, 0, "Stored", false, false, null));

    for (int p = 1; p < info.PageCount; p++) {
      var name = $"pages/page_{p:D5}.bin";
      entries.Add(new ArchiveEntryInfo(idx++, name, info.PageSize, info.PageSize, "Stored", false, false, null));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var streamLen = stream.Length;
    var head = ReadHead(stream);
    var variant = FormatVariant(head);
    var ext = variant == Variant.AccDb ? "accdb" : "mdb";

    void Emit(string name, byte[] data) {
      if (files != null && files.Length > 0 && !MatchesFilter(name, files)) return;
      WriteFile(outputDir, name, data);
    }

    // Stream FULL.* directly — never buffer the whole file.
    var fullName = $"FULL.{ext}";
    if (files == null || files.Length == 0 || MatchesFilter(fullName, files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, fullName);
      var d = Path.GetDirectoryName(fullPath);
      if (d != null) Directory.CreateDirectory(d);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }

    if (!TryBuildInfo(variant, streamLen, out var info)) {
      Emit("metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(streamLen, info, variant, parseStatus: "partial")));
      return;
    }

    Emit("metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(streamLen, info, variant, parseStatus: "ok")));

    // page_00_header: the first page in its entirety (2 KB or 4 KB).
    var headerLen = (int)Math.Min(info.PageSize, streamLen);
    var pageZero = new byte[headerLen];
    stream.Seek(0, SeekOrigin.Begin);
    stream.ReadExactly(pageZero, 0, headerLen);
    Emit("page_00_header.bin", pageZero);

    // MSysObjects pointer: 4-byte LE at offset 0x2C.
    if (head.Length >= 0x2C + 4) {
      uint msys = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x2C, 4));
      Emit("msysobjects_pointer.txt",
        Encoding.UTF8.GetBytes($"msysobjects_root_page={msys}\n"));
    }

    // Per-page extraction via seek+read. Page buffer bounded to 4 KB.
    var pageBuf = new byte[info.PageSize];
    for (int p = 1; p < info.PageCount; p++) {
      long off = (long)p * info.PageSize;
      if (off + info.PageSize > streamLen) break;
      var name = $"pages/page_{p:D5}.bin";
      if (files != null && files.Length > 0 && !MatchesFilter(name, files)) continue;
      stream.Seek(off, SeekOrigin.Begin);
      stream.ReadExactly(pageBuf, 0, info.PageSize);
      var copy = new byte[info.PageSize];
      Buffer.BlockCopy(pageBuf, 0, copy, 0, info.PageSize);
      WriteFile(outputDir, name, copy);
    }
  }

  private static byte[] ReadHead(Stream stream) {
    stream.Seek(0, SeekOrigin.Begin);
    var len = (int)Math.Min(HeadPeekSize, stream.Length);
    var buf = new byte[len];
    var read = 0;
    while (read < len) {
      var n = stream.Read(buf, read, len - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  private enum Variant { Unknown, Jet3, Jet4, AccDb }

  private static Variant FormatVariant(byte[] head) {
    if (head.Length < 4 + 15) return Variant.Unknown;
    if (MatchesAt(head, 4, JetSignature)) {
      // Jet version byte is at offset 0x14.
      byte ver = head.Length > 0x14 ? head[0x14] : (byte)0;
      return ver <= 0 ? Variant.Jet3 : Variant.Jet4;
    }
    if (MatchesAt(head, 4, AceSignature)) return Variant.AccDb;
    return Variant.Unknown;
  }

  private static bool MatchesAt(byte[] data, int offset, byte[] needle) {
    if (offset + needle.Length > data.Length) return false;
    for (int i = 0; i < needle.Length; i++)
      if (data[offset + i] != needle[i]) return false;
    return true;
  }

  private static bool TryBuildInfo(Variant variant, long streamLen, out MdbInfo info) {
    info = default;
    if (variant == Variant.Unknown) return false;

    // Jet 3 uses 2 KB pages; Jet 4 and ACCDB use 4 KB pages.
    int pageSize = variant == Variant.Jet3 ? 2048 : 4096;
    if (streamLen < pageSize) return false;

    long totalPages = streamLen / pageSize;
    info = new MdbInfo {
      PageSize = pageSize,
      PageCount = (int)Math.Min(totalPages, int.MaxValue),
      Variant = variant,
    };
    return true;
  }

  private static string BuildMetadataIni(long fileSize, MdbInfo info, Variant variant, string parseStatus) {
    var sb = new StringBuilder();
    sb.AppendLine("[mdb]");
    sb.AppendLine($"parse_status={parseStatus}");
    sb.AppendLine($"file_size={fileSize}");
    sb.AppendLine($"format={variant switch {
      Variant.Jet3 => "jet3",
      Variant.Jet4 => "jet4",
      Variant.AccDb => "accdb",
      _ => "unknown",
    }}");
    if (parseStatus == "ok") {
      sb.AppendLine($"page_size={info.PageSize}");
      sb.AppendLine($"database_pages={info.PageCount}");
    }
    return sb.ToString();
  }

  private struct MdbInfo {
    public int PageSize;
    public int PageCount;
    public Variant Variant;
  }
}
