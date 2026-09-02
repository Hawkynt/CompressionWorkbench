#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.DiskImage;
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
///         The fresh volume carries a real ODS-2 home block at LBN 1 plus a workbench-layout
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
public sealed class OpenVmsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  /// <summary>
  /// Sole tunable the ODS-2 writer honours: the 12-character home-block volume
  /// label (HM2$T_VOLNAME). Everything else in the workbench-layout geometry is
  /// fixed. An empty label falls back to the writer default ("SCRATCH").
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 12),
  ];

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "OpenVms";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "OpenVMS Files-11";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".ods2";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ods2", ".ods5", ".vmsdisk"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
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
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "DEC/VMS Files-11 (ODS-2) — clean-room writer + reader + in-place Add/Remove/Replace " +
    "modifier sharing the workbench-layout geometry (BITMAP.SYS, INDEXF.SYS, 000000.DIR at fixed " +
    "LBNs). Honest scope: emitted volumes are not OpenVMS-mountable — home-block " +
    "HM2$W_CHECKSUM1/CHECKSUM2, FH FILECHAR/RECATTR bundles, and ODS-2 variable-length " +
    "directory records remain deferred.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    OpenVmsVolume? volume = null;
    try {
      volume = new OpenVmsVolume(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.disk", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    using (volume) {
      // A volume we wrote ourselves lists exactly its user files. Surfacing the
      // synthetic header entries alongside them would make every rebuild
      // (shrink, defrag) fold them back in as real files, so they stay on the
      // carver path — foreign images, where the header IS all we can offer.
      var idx = 0;
      if (volume.IsCwbVolume) {
        foreach (var e in volume.Entries)
          entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", false, false, null));
        return entries;
      }

      OpenVmsHomeBlock hb;
      try {
        hb = OpenVmsHomeBlock.TryParse(volume.Metadata);
      } catch {
        entries.Add(new ArchiveEntryInfo(0, "FULL.disk", volume.Length, volume.Length, "stored", false, false, null));
        entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
        return entries;
      }

      entries.Add(new ArchiveEntryInfo(idx++, "FULL.disk", volume.Length, volume.Length, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
      if (hb.Valid)
        entries.Add(new ArchiveEntryInfo(idx++, "home_block.bin", hb.RawBytes.LongLength, hb.RawBytes.LongLength, "stored", false, false, null));
    }

    return entries;
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    OpenVmsVolume? volume = null;
    try {
      volume = new OpenVmsVolume(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    using (volume) {
      if (volume.IsCwbVolume) {
        foreach (var e in volume.Entries) {
          if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
          Directory.CreateDirectory(outputDir);
          using var target = File.Create(Path.Combine(outputDir, Path.GetFileName(e.Name)));
          volume.ExtractTo(e, target);
        }
        return;
      }

      OpenVmsHomeBlock hb;
      try {
        hb = OpenVmsHomeBlock.TryParse(volume.Metadata);
      } catch {
        this.WriteFullDisk(volume, outputDir, files);
        WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
        return;
      }

      this.WriteFullDisk(volume, outputDir, files);
      WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hb), files);
      if (hb.Valid)
        WriteIfMatch(outputDir, "home_block.bin", hb.RawBytes, files);
    }
  }

  private void WriteFullDisk(OpenVmsVolume volume, string outputDir, string[]? files) {
    if (files is { Length: > 0 } && !MatchesFilter("FULL.disk", files)) return;
    Directory.CreateDirectory(outputDir);
    using var target = File.Create(Path.Combine(outputDir, "FULL.disk"));
    volume.CopyTo(0, target, volume.Length);
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
    OpenVmsVolume volume;
    try {
      volume = new OpenVmsVolume(archive);
    } catch {
      return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
    }

    using (volume) {
      if (string.Equals(entryName, "FULL.disk", StringComparison.OrdinalIgnoreCase))
        return new BoundedEntryStream(new ReadOnlyStreamSlice(archive, 0, volume.Length), volume.Length, leaveOpen: false);

      if (string.Equals(entryName, "home_block.bin", StringComparison.OrdinalIgnoreCase)) {
        try {
          var hb = OpenVmsHomeBlock.TryParse(volume.Metadata);
          if (hb.Valid)
            return new BoundedEntryStream(new MemoryStream(hb.RawBytes, writable: false), hb.RawBytes.LongLength, leaveOpen: false);
        } catch {
          // fall through to user-file search
        }
      }

      try {
        if (volume.IsCwbVolume) {
          var normalized = OpenVmsWriter.NormalizeName(entryName);
          foreach (var e in volume.Entries) {
            if (!string.Equals(e.Name, normalized, StringComparison.OrdinalIgnoreCase)) continue;
            var fh = volume.ReadFileHeader(e.FileId);
            if (fh == null || !fh.InUse) break;

            // Every file this writer and its in-place modifier emit occupies one
            // contiguous run, so the entry is a plain window onto the volume —
            // no copy, whatever the file's size.
            if (fh.Extents.Count == 1) {
              var origin = OpenVmsLayout.LbnToByteOffset(fh.Extents[0].StartLbn);
              var span = Math.Min(fh.Size, Math.Max(0, volume.Length - origin));
              return new BoundedEntryStream(new ReadOnlyStreamSlice(archive, origin, span), span, leaveOpen: false);
            }

            using var scratch = new MemoryStream();
            volume.ExtractTo(e, scratch);
            var bytes = scratch.ToArray();
            return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.LongLength, leaveOpen: false);
          }
        }
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

  /// <summary>
  /// Builds a fresh ODS-2 volume containing <paramref name="inputs"/> as
  /// user files in 000000.DIR. Each file is a contiguous extent.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var label = options?.GetOption("VolumeLabel", "SCRATCH") ?? "SCRATCH";
    if (string.IsNullOrEmpty(label)) label = "SCRATCH";
    var files = new List<(string Name, FilePayload Payload)>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = Path.GetFileName(input.ArchiveName);
      if (input.InMemoryContent is { } bytes) {
        files.Add((name, FilePayload.FromBytes(bytes)));
        continue;
      }
      // Sized from the file on disk and opened only while it is being copied —
      // a volume never holds more than one buffer of any input.
      var path = input.FullPath;
      files.Add((name, FilePayload.FromStream(new FileInfo(path).Length, () => File.OpenRead(path))));
    }

    new OpenVmsWriter().BuildTo(output, files, label);
  }

  /// <summary>
  /// Adds (or replaces by name) caller files in-place via
  /// <see cref="OpenVmsInPlaceModifier"/>. Untouched LBNs in <paramref name="archive"/>
  /// remain byte-identical.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in — and where it can still edit, it has no room
    // to grow a full volume. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

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
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

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


  // ── IFilesystemExtentMap + IWipeEmpty ─────────────────────────────────

  /// <summary>
  /// Reports the volume's layout: the fixed metadata prefix — boot block, home
  /// block, BITMAP.SYS, INDEXF.SYS and the root directory — then each file's
  /// contiguous run of LBNs. Everything else is free.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    List<DefragBlockInfo> result = [];
    try {
      if (image.CanSeek) image.Position = 0;
      using var volume = new OpenVmsVolume(image);
      if (!volume.IsCwbVolume) return [];

      result.Add(new DefragBlockInfo(0, OpenVmsLayout.MetadataBytes,
        DefragBlockKind.MetadataReserved, "Boot block, home block, BITMAP.SYS, INDEXF.SYS and 000000.DIR"));

      foreach (var e in volume.Entries) {
        var fh = volume.ReadFileHeader(e.FileId);
        if (fh is not { InUse: true }) continue;
        long written = 0;
        foreach (var ext in fh.Extents) {
          var take = Math.Min(ext.Count * (long)OpenVmsLayout.BlockSize, fh.Size - written);
          if (take <= 0) break;
          result.Add(new DefragBlockInfo(OpenVmsLayout.LbnToByteOffset(ext.StartLbn), take,
            DefragBlockKind.Used, e.Name));
          written += take;
        }
      }
    } catch {
      return [];
    }
    return result;
  }

  /// <summary>
  /// Zeros every byte no live file occupies. A file is one contiguous run of LBNs,
  /// so the gaps between runs — and the tail past the last one — are free space.
  /// The run is reported at its logical length, so the padding to the block
  /// boundary is wiped with it.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    _ = wipeDeletedEntries;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }


  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  /// <inheritdoc />
    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Moves only the files that are out of place, rewriting each one's retrieval
  /// pointer as its blocks arrive. The pass is kept only if every payload still
  /// reads back: it can refuse partway — a header it cannot find leaves bytes
  /// moved with nothing naming them — and the volume goes back as it was then.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => {
        var reader = new OpenVmsReader(stream);
        return reader.Entries.Select(reader.Extract).ToList();
      },
      inPlace: () => this.DefragmentWithPlanner(archive, options),
      rebuild: () => { /* the volume is put back as it was */ });
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new OpenVmsBlockMover();

    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    // A file described by several retrieval pointers needs its whole header map
    // restated, which this pass cannot do.
    var runsPerOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in extents) {
      if (extent.Kind != DefragBlockKind.Used || extent.FileName is not { } owner) continue;
      runsPerOwner.TryGetValue(owner, out var count);
      runsPerOwner[owner] = count + 1;
    }
    var fragmented = runsPerOwner.Count(kv => kv.Value > 1);
    if (fragmented > 0)
      throw new NotSupportedException(
        $"OpenVMS: {fragmented} file(s) span more than one retrieval pointer.");

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) return;

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      archive.Length, reinitAfterMove: null);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

}
