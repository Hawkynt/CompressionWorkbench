#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.RomFs;

/// <summary>
/// R/W descriptor for Linux ROMFS images — the "-rom1fs-" packed read-only
/// filesystem used for boot/initrd media.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.kernel.org/doc/html/latest/filesystems/romfs.html</c> — kernel documentation — includes the complete on-disk layout</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/romfs</c> — Linux reference implementation</description></item>
///   <item><description><c>genromfs</c> — the canonical image-builder tool</description></item>
/// </list>
/// </summary>
public sealed class RomFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// Sole tunable the ROMFS writer honours: the volume name stored in the
  /// superblock right after the "-rom1fs-" magic. ROMFS is a packed read-only
  /// image with no allocation-unit knob. An empty label falls back to the
  /// writer default ("romfs").
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
  ];

  public string Id => "RomFs";
  public string DisplayName => "ROMFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".romfs";
  public IReadOnlyList<string> Extensions => [".romfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("-rom1fs-"u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("romfs", "ROMFS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Linux ROM filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new RomFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new RomFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
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
    var r = new RomFsReader(archive);
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
    using var w = new RomFsWriter(output, leaveOpen: true);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the image out; reading a large input
      // into a byte[] would cap the image at what a list can address.
      var name = Path.GetFileName(info.ArchiveName);
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    var volumeName = options?.GetOption("VolumeLabel", "");
    w.Finish(string.IsNullOrEmpty(volumeName) ? "romfs" : volumeName);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing RomFs image. Uses
  /// <see cref="RomFsModifier"/> for in-place append. Falls back to rebuild
  /// if the in-place path fails.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      foreach (var (name, data) in FormatHelpers.FilesOnly(inputs)) {
        RomFsModifier.RemoveFile(archive, name);
        RomFsModifier.AddFile(archive, name, data);
      }
    } catch {
      archive.Position = 0;
      ModifyRebuilder.Add(archive, inputs,
        readEntries: stream => {
          var r = new RomFsReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        buildImage: BuildImage, largeVolumeCreator: this);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing RomFs image. ROMFS entries
  /// are inline with headers + data, so unlinking the first entry requires
  /// rebuilding. We use rebuild for Remove to handle all edge cases reliably.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new RomFsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage, largeVolumeCreator: this);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new RomFsBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new RomFsBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Defragments a RomFs image. Falls back to rebuild since ROMFS entries
  /// are tightly packed with inline data — in-place reordering is complex.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // An image too large to materialise goes through the streaming rebuilder;
    // BuildImage assembles the whole thing in a MemoryStream.
    // Every mode streams above the cap: end-pack and carve-hole order their
    // entries from scratch inside the rebuilder, so none of them falls back
    // to a buffered rebuild the volume is too large for.
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      RomFsWriter? streamWriter = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => {
          var r = new RomFsReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
        },
        beginWrite: s2 => streamWriter = new RomFsWriter(s2, leaveOpen: true),
        // As a stream factory, not inline: an inline payload would go back into
        // the buffer this path exists to avoid.
        writeEntry: (name, data) => streamWriter!.AddStreamingFile(
          name, data.LongLength, () => new MemoryStream(data, writable: false)),
        finishWrite: () => { streamWriter!.Finish(); streamWriter.Dispose(); });
      return;
    }

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new RomFsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage);
  }

  /// <summary>Largest image a defrag will rebuild through a byte[].</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    using var w = new RomFsWriter(ms, leaveOpen: true);
    foreach (var (n, d) in files) w.AddFile(n, d);
    w.Finish();
    return ms.ToArray();
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => RomFsExtentMap.Enumerate(image);

  /// <summary>
  /// Zeros the unused space in a ROMFS image: the 16-byte alignment padding
  /// after each file's data and any trailing slack before the image's declared
  /// full size. ROMFS is a packed, read-only image — every file's data is
  /// stored byte-exact (no cluster rounding), so there is no cluster tip to
  /// wipe; cluster-tip wiping is therefore not applicable and is a no-op here.
  /// All file headers, names and live data are reported as live extents by the
  /// extent map, so the generic wiper only touches genuine gaps.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = RomFsExtentMap.Enumerate(image);

    // Tips are not applicable to a packed read-only image; pass null lookup so
    // only the inter-record alignment padding and trailing slack are zeroed.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
