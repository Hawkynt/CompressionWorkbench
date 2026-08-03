#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.VxFs;

/// <summary>
/// Read-only descriptor for VxFS (Veritas File System), used by HP-UX,
/// Solaris, and AIX (and a Linux read-only port). Walking the OLT (Object
/// Location Table) → FSH (FileSet Header) → IAU (Inode Allocation Unit)
/// chain to extract user files is explicitly out of scope (multi-week
/// effort) — this descriptor surfaces:
/// <list type="bullet">
///   <item><description><c>FULL.vxfs</c> — the raw image bytes</description></item>
///   <item><description><c>metadata.ini</c> — parsed superblock fields</description></item>
///   <item><description><c>superblock.bin</c> — 1 KB capture of the on-disk superblock</description></item>
/// </list>
///
/// Detection: 4-byte magic <c>0xA501FCF5</c> at offset 1024. The magic is
/// stored in the natural endianness of the host that wrote the volume —
/// little-endian on x86 / Linux, big-endian on HP-UX PA-RISC and Solaris
/// SPARC. Both signature variants are registered.
///
/// Create / Modify / Defragment: <see cref="NotSupportedException"/> — the
/// descriptor is read-only.
///
/// References:
/// <list type="bullet">
///   <item><description>Linux kernel <c>fs/freevxfs/vxfs.h</c> + <c>vxfs_super.c</c></description></item>
///   <item><description>HP-UX "VxFS Administrator's Guide" (Veritas / Symantec)</description></item>
///   <item><description>Wikipedia "Veritas File System"</description></item>
/// </list>
/// </summary>
/// <summary>
/// Why there is nothing here to lay out again.
/// </summary>
/// <remarks>
/// This reads the superblock and the header region around it, and nothing
/// else. There is no file enumeration and no extraction, so nothing in this
/// implementation knows where any file's bytes are — which is what a layout
/// pass would have to be planned against. Writing that reader comes first; the
/// defragmentation would follow from it.
public sealed class VxFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "VxFs";
  public string DisplayName => "VxFS (Veritas)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".vxfs";
  public IReadOnlyList<string> Extensions => [".vxfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Little-endian (x86 / Linux native) — vs_magic = 0xA501FCF5 → F5 FC 01 A5.
    new(VxFsReader.MagicLE, Offset: VxFsReader.SuperblockOffset, Confidence: 0.90f),
    // Big-endian (HP-UX PA-RISC / Solaris SPARC native) — A5 01 FC F5.
    new(VxFsReader.MagicBE, Offset: VxFsReader.SuperblockOffset, Confidence: 0.90f),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "VxFS (Veritas File System) image — header-surface read-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.vxfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    VxFsReader reader;
    try {
      using var ms = new MemoryStream(image, writable: false);
      reader = new VxFsReader(ms);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.vxfs", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.vxfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (reader.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "superblock.bin", reader.HeaderRaw.LongLength, reader.HeaderRaw.LongLength, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    VxFsReader reader;
    try {
      using var ms = new MemoryStream(image, writable: false);
      reader = new VxFsReader(ms);
    } catch {
      WriteIfMatch(outputDir, "FULL.vxfs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.vxfs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(reader), files);
    if (reader.Valid)
      WriteIfMatch(outputDir, "superblock.bin", reader.HeaderRaw, files);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// There is nothing here to lay out again, and the reason is not the missing
  /// writer.
  /// </summary>
  /// <remarks>
  /// A pass that moves blocks needs no writer — it needs to know where a file's
  /// bytes are, and that is what this reader cannot say. It parses the
  /// superblock at offset 1024 and nothing beyond it: no inode list, no extent
  /// descriptors, so what it lists is the volume as a whole plus that
  /// superblock. Until something can name a byte as belonging to a file, a pass
  /// has no subject to move.
  /// </remarks>
  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException(
      "VxFS defragmentation has nothing to move: this reader parses the superblock and no inode " +
      "list or extent descriptors, so no byte can be named as belonging to a file.");

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(VxFsReader r) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={r.ParseStatus}\n");
    if (r.Valid) {
      b.Append(ic, $"endianness={(r.IsBigEndian ? "big" : "little")}\n");
      b.Append(ic, $"vs_magic=0x{r.VsMagic:X8}\n");
      b.Append(ic, $"vs_version={r.VsVersion}\n");
      b.Append(ic, $"vs_mtime={r.VsMtime}\n");
      b.Append(ic, $"vs_ctime={r.VsCtime}\n");
      b.Append(ic, $"vs_blocksize={r.VsBlockSize}\n");
      b.Append(ic, $"vs_size={r.VsSize}\n");
      b.Append(ic, $"vs_dsize={r.VsDsize}\n");
      b.Append(ic, $"vs_old_nau={r.VsOldNau}\n");
      b.Append(ic, $"vs_immedlen={r.VsImmedLen}\n");
      b.Append(ic, $"vs_ndaddr={r.VsNdAddr}\n");
      b.Append(ic, $"vs_firstau={r.VsFirstAu}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // 64 KB cap — superblock at offset 1024 fits easily; speculative carver scans
  // therefore can't pull a multi-GB image into memory.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
