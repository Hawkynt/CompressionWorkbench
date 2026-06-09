#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.OpenVms;

/// <summary>
/// Read-only descriptor for OpenVMS Files-11 (ODS-2 + ODS-5) volume images
/// — the DEC/VMS native FS used on VAX, Alpha, Itanium and (from 2020) x86-64
/// OpenVMS systems. Surfaces the parsed home block as a structured metadata
/// bundle plus the raw image. Walking the index file and per-file headers to
/// produce a real directory tree is multi-week work and out of scope here.
/// </summary>
public sealed class OpenVmsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "OpenVms";
  public string DisplayName => "OpenVMS Files-11";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ods2";
  public IReadOnlyList<string> Extensions => [".ods2", ".ods5", ".vmsdisk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "DECFILE11A " ASCII at offset 0x1E8 (488) inside the home block which itself
    // sits at logical block 1 (offset 512) → absolute file offset 1000 (0x3E8).
    // Confidence raised from 0.7 → 0.85 so the FilesystemCarver's MinConfidence
    // default (0.5) doesn't false-trigger this reader on random buffers — at the
    // larger 11-byte width false-match rate is already negligible, but keeping
    // it firmly above the median scanner threshold means fewer wasted reader
    // invocations during forensic scans of 10 MB+ random/garbage payloads.
    new("DECFILE11A "u8.ToArray(), Offset: 1000, Confidence: 0.85),
    new("DECFILE11B "u8.ToArray(), Offset: 1000, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "DEC/VMS Files-11 (ODS-2) — home-block parse + WORM emit of a clean-room ODS-2 layout " +
    "(boot block at LBN 0, real home block at LBN 1 with DECFILE11A magic at 0x1E8, " +
    "CWB-OVMS-WB file table at LBN 2). Note: emitted images carry every documented home-block " +
    "field but not a valid INDEXF.SYS / BITMAP.SYS / checksum1 — real OpenVMS would reject the " +
    "volume at mount. ODS-5 (DECFILE11B) write support is not implemented.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.disk", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    OpenVmsHomeBlock hb;
    try {
      hb = OpenVmsHomeBlock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.disk", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.disk", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (hb.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "home_block.bin", hb.RawBytes.LongLength, hb.RawBytes.LongLength, "stored", false, false, null));
    var fileTable = OpenVmsFileTable.TryParse(image);
    foreach (var f in fileTable.Entries)
      entries.Add(new ArchiveEntryInfo(idx++, f.Name, f.Size, f.Size, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    OpenVmsHomeBlock hb;
    try {
      hb = OpenVmsHomeBlock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.disk", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.disk", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hb), files);
    if (hb.Valid)
      WriteIfMatch(outputDir, "home_block.bin", hb.RawBytes, files);
    var fileTable = OpenVmsFileTable.TryParse(image);
    foreach (var f in fileTable.Entries)
      WriteIfMatch(outputDir, f.Name, fileTable.Extract(image, f), files);
  }

  /// <summary>
  /// WORM-emits a fresh ODS-2 volume image carrying the supplied
  /// <paramref name="inputs"/>. The home block at LBN 1 carries the canonical
  /// DECFILE11A format string, structure level 0x0202, cluster size 1, owner
  /// UIC [1,1]. Bundled files round-trip through the CWB-OVMS-WB file table at
  /// LBN 2 plus a flat data area at byte offset 8192 onwards.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var w = new OpenVmsWriter(output, leaveOpen: true);
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Opens a synthetic entry as a bounded stream over its actual bytes.
  /// Supports <c>FULL.disk</c> (whole image) and <c>home_block.bin</c> (parsed
  /// 512-byte home block). Reads past the entry's logical size return 0 (EOF).
  /// User files inside INDEXF.SYS are not addressable here — that requires the
  /// deferred ODS-2 file-header walker.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    byte[] image;
    try {
      image = ReadAll(archive);
    } catch {
      return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
    }

    if (string.Equals(entryName, "FULL.disk", StringComparison.OrdinalIgnoreCase))
      return new BoundedEntryStream(new MemoryStream(image, writable: false), image.LongLength, leaveOpen: false);

    if (string.Equals(entryName, "home_block.bin", StringComparison.OrdinalIgnoreCase)) {
      try {
        var hb = OpenVmsHomeBlock.TryParse(image);
        if (hb.Valid)
          return new BoundedEntryStream(new MemoryStream(hb.RawBytes, writable: false), hb.RawBytes.LongLength, leaveOpen: false);
      } catch {
        // fall through
      }
    }

    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(OpenVmsHomeBlock hb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(hb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"home_block_offset={hb.HomeBlockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"format_string={hb.FormatString}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volume_label={hb.VolumeLabel}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"structure_level=0x{hb.StructureLevel:X4}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"structure_name={hb.StructureName}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"cluster_size={hb.ClusterSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"max_files={hb.MaxFiles}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"owner_uic=0x{hb.OwnerUic:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"index_bitmap_lbn={hb.IndexBitmapLbn}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  // Bounded — Files-11 home block lives at offset 512 of the volume; 64 KB is
  // overkill for header surfacing. When the writer's CWB-OVMS-WB extension is
  // present we extend the cap to 64 MB so bundled file payloads round-trip
  // through the file table, while still bounding speculative carver scans.
  private const int ReadCap = 64 * 1024 * 1024;

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < ReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
