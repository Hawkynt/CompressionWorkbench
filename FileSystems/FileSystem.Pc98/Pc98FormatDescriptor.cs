#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Pc98;

/// <summary>
/// Descriptor for NEC PC-98 DOS disk images. PC-98 disks use a
/// FAT12/16-like filesystem with a vendor-specific Initial Program
/// Loader (IPL) block at sector 0 — detection is by the "NECIPL" ASCII
/// signature at file offset 0.
///
/// <para>PC-98 supports subdirectories per the FAT12 model. The current
/// minimal writer emits a single flat root directory only;
/// hierarchical writes are deferred. The reader handles subdirectory
/// dirents at the root by surfacing them as entries with
/// <see cref="ArchiveEntryInfo.IsDirectory"/> set, but does not recurse
/// into them.</para>
///
/// <para>Capabilities: read + write (flat-only writer), defragment via
/// extract-and-rebuild, free-space wiping driven by the extent map, and
/// creation-options schema for media type / bytes-per-sector / sectors-per-cluster
/// / volume label.</para>
/// </summary>
public sealed class Pc98FormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
  IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  public string Id => "Pc98";
  public string DisplayName => "NEC PC-98 DOS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".hdm";
  public IReadOnlyList<string> Extensions => [".hdm", ".fdi", ".d88"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("NECIPL"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "NEC PC-98 DOS disk filesystem — FAT12 variant prefixed by an 'NECIPL' Initial Program Loader block.";

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for PC-98 creation: media type (controls the OEM
  /// label in the BPB), bytes per sector, sectors per cluster, and
  /// volume label.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "MediaType",
      DisplayName: "Media type",
      Kind: FormatOptionKind.Enum,
      Default: "HDM",
      AllowedValues: ["HDM", "FDI", "D88"],
      Description: "HDM = 1.25/1.44 MB hard disk image. FDI = Floppy Disk Image. D88 = NEC 88-series."),
    new FormatOptionDescriptor(
      Key: "BytesPerSector",
      DisplayName: "Bytes per sector",
      Kind: FormatOptionKind.Enum,
      Default: "512",
      AllowedValues: ["256", "512", "1024"],
      Description: "Bytes per sector. 512 is the safest choice for round-tripping with the reader."),
    FilesystemSchemaPresets.PowerOfTwoSize(
      key: "SectorsPerCluster",
      displayName: "Sectors per cluster",
      min: 1, max: 16,
      defaultLabel: "Auto",
      description: "Sectors per cluster. Auto picks the smallest that fits with <= 5 % slack."),
    FilesystemSchemaPresets.VolumeLabel(maxChars: 11),
  ];

  // ── IArchiveFormatOperations ────────────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new Pc98Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Pc98Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  // ── IArchiveCreatable ───────────────────────────────────────────────────

  /// <summary>
  /// Opens a single filesystem entry as a bounded read-only stream. The
  /// reader produces the decoded file bytes by walking the entry's extent
  /// or block chain; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so cluster/extent slack past the entry's
  /// end is physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new Pc98Reader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var w = new Pc98Writer();
    var media = options?.GetOption("MediaType", "HDM") ?? "HDM";
    w.SetMediaType(media);

    var bpsStr = options?.GetOption("BytesPerSector", "512") ?? "512";
    if (int.TryParse(bpsStr, out var bps)) w.SetBytesPerSector(bps);

    var spcStr = options?.GetOption("SectorsPerCluster", "Auto") ?? "Auto";
    var fileSizes = inputs.Where(i => !i.IsDirectory).Select(i => (long)i.ReadContent().Length).ToList();
    var auto = Pc98Optimizer.Find(fileSizes);
    var spc = spcStr is "Auto" or "0" ? auto.SectorsPerCluster : FilesystemSchemaPresets.ParseSize(spcStr) / 512;
    if (spc <= 0) spc = auto.SectorsPerCluster;
    w.SetSectorsPerCluster(spc);

    var label = options?.GetOption("VolumeLabel", "");
    w.SetVolumeLabel(label);

    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new Pc98Reader(stream);
        return r.Entries.Where(e => !e.IsDirectory)
                        .Select(e => (e.Name, r.Extract(e)))
                        .ToList();
      },
      buildImage: files => {
        var w = new Pc98Writer();
        var sizes = files.Select(f => (long)f.Data.Length).ToList();
        var layout = Pc98Optimizer.Find(sizes);
        w.SetSectorsPerCluster(layout.SectorsPerCluster);
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  // ── IFilesystemExtentMap ───────────────────────────────────────────────

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => Pc98ExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all bytes not claimed by the IPL block, the FAT, the root
  /// directory, or a live file's cluster run. Cluster-tip wiping uses
  /// the directory entry's file size when
  /// <paramref name="wipeClusterTips"/> is true.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? sizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new Pc98Reader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in reader.Entries)
          if (!e.IsDirectory)
            sizeMap[e.Name] = e.Size;
        sizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        sizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = Pc98ExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, sizeLookup);
  }
}
