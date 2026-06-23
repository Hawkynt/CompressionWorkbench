#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Gemdos;

/// <summary>
/// Atari ST GEMDOS disk image descriptor. GEMDOS is a FAT12 variant: the on-disk
/// layout is exactly MS-DOS FAT12 (BPB at offset 11 onwards, two FAT copies,
/// fixed-size root directory, 8.3 dirents, free-block-chain allocation), but the
/// jump byte at offset 0 is <c>0x60</c> (Motorola 68000 <c>BRA.S</c>) instead
/// of <c>0xEB</c>/<c>0xE9</c> (x86 <c>JMP</c>). The reader and writer here delegate
/// to the FAT12 implementation in <see cref="FileSystem.Fat"/> and re-present
/// the jump byte at the boundary.
///
/// <para><b>Hierarchy support.</b> GEMDOS supports subdirectories via standard
/// FAT12 directory entries (attribute bit 4 = 0x10). The reader / writer inherit
/// full tree support from <see cref="FileSystem.Fat.FatReader"/> /
/// <see cref="FileSystem.Fat.FatWriter"/>.</para>
///
/// <para><b>Defrag / Purge / Conversion.</b> Driven by the rebuild-based pattern
/// in <see cref="Compression.Registry.DefragRebuilder"/>; conversion is unlocked
/// for free via <see cref="IArchiveCreatable"/>. Purge zeros all free clusters
/// + cluster-tip slack via the FAT extent map.</para>
///
/// <para><b>Spec.</b> Atari ST Internals (Brückmann, Englisch, Gerits, 1986),
/// GEMDOS disk format chapter; standard FAT12 spec (FATGEN103) for the BPB
/// and on-disk layout.</para>
/// </summary>
public sealed class GemdosFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  public string Id => "Gemdos";
  public string DisplayName => "GEMDOS (Atari ST)";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: GEMDOS is FAT12; add/remove edit the FAT, clusters and root directory in
  // place via FatModifier / FatRemover (the 0x60 jump byte and existing files stay
  // byte-identical), with a re-pack only as a structural fallback. See FormatCapabilities.cs.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsDirectories |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".st";
  public IReadOnlyList<string> Extensions => [".st", ".stx", ".dim"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x60 at offset 0 (m68k BRA.S branch). 4-byte signature with masked tail —
    // we only require the jump byte to match; the displacement bytes are
    // image-specific. Confidence 0.55 because a single byte is weak; the
    // reader's BPB validation upgrades certainty downstream.
    new([0x60, 0x00, 0x00, 0x00],
        Offset: 0, Confidence: 0.55,
        Mask: [0xFF, 0x00, 0x00, 0x00]),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Atari ST GEMDOS — FAT12 variant with 0x60 BRA.S jump byte.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "BytesPerSector",
      DisplayName: "Bytes per sector",
      Kind: FormatOptionKind.Enum,
      Default: "512",
      AllowedValues: ["256", "512", "1024"],
      Description: "Atari TOS accepts 256 / 512 / 1024 bytes per sector. 512 is universal across emulators and real hardware."),
    new FormatOptionDescriptor(
      Key: "SectorsPerCluster",
      DisplayName: "Sectors per cluster",
      Kind: FormatOptionKind.Enum,
      Default: "2",
      AllowedValues: ["1", "2", "4"],
      Description: "Allocation unit size in sectors. Two-sector clusters are the GEMDOS default for floppy media."),
    new FormatOptionDescriptor(
      Key: "TotalSectors",
      DisplayName: "Image size",
      Kind: FormatOptionKind.Enum,
      Default: "1440",
      AllowedValues: ["720", "1440", "2880", "5760"],
      Description: "Total sectors. 720 = 360 KB SS DD, 1440 = 720 KB DS DD, 2880 = 1.44 MB DS HD, 5760 = 2.88 MB DS ED."),
    new FormatOptionDescriptor(
      Key: "RootEntries",
      DisplayName: "Root directory entries",
      Kind: FormatOptionKind.Enum,
      Default: "112",
      AllowedValues: ["64", "112", "224"],
      Description: "Maximum directory entries in the root directory (FAT12 root is a fixed-size region)."),
    FilesystemSchemaPresets.VolumeLabel(maxChars: 11),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new GemdosReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new GemdosReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

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
    var r = new GemdosReader(archive);
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
    options ??= new FormatCreateOptions();

    var bps   = options.GetOptionInt("BytesPerSector", 512);
    var spc   = options.GetOptionInt("SectorsPerCluster", 2);
    var total = options.GetOptionInt("TotalSectors", 1440);
    var root  = options.GetOptionInt("RootEntries", 112);
    var label = options.GetOption("VolumeLabel", "");

    var w = new GemdosWriter();
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      w.AddFile(input.ArchiveName, input.ReadContent(),
                input.InMemoryContent != null ? null : File.GetLastWriteTime(input.FullPath));
    var disk = w.Build(
      totalSectors: total,
      bytesPerSector: bps,
      sectorsPerCluster: spc,
      rootEntries: root,
      volumeLabel: string.IsNullOrEmpty(label) ? null : label);
    output.Write(disk);
  }

  /// <summary>
  /// Adds — or replaces by name — files in an existing GEMDOS image.
  /// Delegates to <see cref="GemdosInPlaceModifier.AddFiles"/> which
  /// re-packs the image with the existing files plus the new ones
  /// while preserving the 0x60 BRA.S jump byte.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var pairs = new List<(string Name, byte[] Data)>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      pairs.Add((input.ArchiveName, input.ReadContent()));
    }
    GemdosInPlaceModifier.AddFiles(archive, pairs);
  }

  /// <summary>
  /// Removes the named entries from an existing GEMDOS image. Delegates
  /// to <see cref="GemdosInPlaceModifier.RemoveFiles"/> which wipes all
  /// on-disk traces (data clusters, cluster-tip slack, directory entries,
  /// FAT chain entries) while preserving the 0x60 BRA.S jump byte.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => GemdosInPlaceModifier.RemoveFiles(archive, entryNames);

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => GemdosExtentMap.Enumerate(image);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new GemdosReader(stream);
        return r.Entries.Where(e => !e.IsDirectory)
                        .Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new GemdosWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        // Preserve the image's original sector count so rebuild is in-place.
        var totalSectors = (int)(archive.Length / 512);
        if (totalSectors <= 0) totalSectors = 1440;
        return w.Build(totalSectors: totalSectors);
      });
  }

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    // File-size lookup for cluster-tip wiping comes from the GEMDOS directory.
    Func<string, long>? lookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var r = new GemdosReader(image);
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in r.Entries) if (!e.IsDirectory) map[e.Name] = e.Size;
        lookup = n => map.TryGetValue(n, out var s) ? s : -1;
      } catch { lookup = null; }
    }

    image.Position = 0;
    var extents = GemdosExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, lookup);
  }
}
