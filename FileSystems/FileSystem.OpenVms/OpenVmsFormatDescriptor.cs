#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.OpenVms;

/// <summary>
/// Read/write descriptor for OpenVMS Files-11 (ODS-2) volume images.
/// Backed by a clean-room writer / reader / in-place modifier trio that
/// shares the geometry pinned at <see cref="OpenVmsLayout"/>. The
/// descriptor advertises:
/// <list type="bullet">
///   <item><see cref="FormatCapabilities.CanList"/> + <see cref="FormatCapabilities.CanExtract"/>
///         — driven by <see cref="OpenVmsReader"/> walking 000000.DIR.</item>
///   <item><see cref="FormatCapabilities.CanCreate"/> — driven by <see cref="OpenVmsWriter"/>.
///         The fresh volume carries a real ODS-2 home block at LBN 1 plus a CWB-OVMS-WB
///         layout marker at byte 132 of the home block.</item>
///   <item><see cref="FormatCapabilities.CanModify"/> — driven by
///         <see cref="OpenVmsInPlaceModifier"/>. Add / Remove / Replace
///         touch only the BITMAP.SYS sector, the file's INDEXF.SYS slot,
///         the directory block, and the affected data LBNs.</item>
/// </list>
///
/// <para>
/// <b>Honest scope.</b> The emitted volume is not OpenVMS-mountable —
/// the home block's HM2$W_CHECKSUM1/CHECKSUM2 surfaces, the FH FILECHAR
/// and RECATTR bundles, the ODS-2 variable-length directory record
/// format, and the per-file revision-history fields are out of scope.
/// What it IS is a layout the workbench's own writer, reader and in-place
/// modifier can round-trip end-to-end through Add / Remove / Replace.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description>DEC "Files-11 On-Disk Structure Specification" — the canonical ODS-2 spec (archived at Bitsavers)</description></item>
///   <item><description>Kirby McCoy, "VMS File System Internals" (Digital Press, 1990)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Files-11</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class OpenVmsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// Sole tunable the ODS-2 writer honours: the 12-character home-block volume
  /// label (HM2$T_VOLNAME). Everything else in the CWB-OVMS-WB geometry is
  /// fixed. An empty label falls back to the writer default ("CWBVOL").
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 12),
  ];

  public string Id => "OpenVms";
  public string DisplayName => "OpenVMS Files-11";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
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
    "DEC/VMS Files-11 (ODS-2) — clean-room writer + reader + in-place Add/Remove/Replace " +
    "modifier sharing the CWB-OVMS-WB geometry (BITMAP.SYS, INDEXF.SYS, 000000.DIR at fixed " +
    "LBNs). Honest scope: emitted volumes are not OpenVMS-mountable — home-block " +
    "HM2$W_CHECKSUM1/CHECKSUM2, FH FILECHAR/RECATTR bundles, and ODS-2 variable-length " +
    "directory records remain deferred.";

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

    // CWB-OVMS-WB volumes carry real user files in 000000.DIR — surface those.
    try {
      var reader = new OpenVmsReader(image);
      if (reader.IsCwbVolume) {
        foreach (var e in reader.Entries)
          entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", false, false, null));
      }
    } catch {
      // Unrecognised geometry — fall back to header-surface only.
    }

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

    try {
      var reader = new OpenVmsReader(image);
      if (reader.IsCwbVolume) {
        foreach (var e in reader.Entries)
          WriteIfMatch(outputDir, e.Name, reader.Extract(e), files);
      }
    } catch {
      // Unrecognised geometry — only the header surface is extracted.
    }
  }

  /// <summary>
  /// Opens a synthetic or real entry as a bounded read-only stream:
  /// <c>FULL.disk</c> (whole image), <c>home_block.bin</c> (parsed 512-byte
  /// home block), or any of the user-file names listed in 000000.DIR
  /// (assembled from the FH's retrieval pointers).
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
        // fall through to user-file search
      }
    }

    try {
      var reader = new OpenVmsReader(image);
      if (reader.IsCwbVolume) {
        var normalized = OpenVmsWriter.NormalizeName(entryName);
        foreach (var e in reader.Entries) {
          if (!string.Equals(e.Name, normalized, StringComparison.OrdinalIgnoreCase)) continue;
          var bytes = reader.Extract(e);
          return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.LongLength, leaveOpen: false);
        }
      }
    } catch {
      // fall through
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

  /// <summary>
  /// Builds a fresh ODS-2 volume containing <paramref name="inputs"/> as
  /// user files in 000000.DIR. Each file is a contiguous extent.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var label = options?.GetOption("VolumeLabel", "CWBVOL") ?? "CWBVOL";
    if (string.IsNullOrEmpty(label)) label = "CWBVOL";
    var files = FlatFiles(inputs).ToList();
    var image = new OpenVmsWriter().Build(files, label);
    output.Write(image);
  }

  /// <summary>
  /// Adds (or replaces by name) caller files in-place via
  /// <see cref="OpenVmsInPlaceModifier"/>. Untouched LBNs in <paramref name="archive"/>
  /// remain byte-identical.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = Path.GetFileName(input.ArchiveName);
      var data = input.ReadContent();
      OpenVmsInPlaceModifier.ReplaceFile(archive, name, data);
    }
  }

  /// <summary>Removes the named entries in-place via <see cref="OpenVmsInPlaceModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      OpenVmsInPlaceModifier.RemoveFile(archive, name, wipeData: true);
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

  // The read-only header surface is bounded for forensic carver use; the
  // R/W operations bypass this cap by reading the full volume directly.
  private const int HeaderReadCap = 16 * 1024 * 1024;  // 16 MB covers the default 4 MB volume with headroom

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
