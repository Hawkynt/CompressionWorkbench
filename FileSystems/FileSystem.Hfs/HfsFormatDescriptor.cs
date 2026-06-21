#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Hfs;

public sealed class HfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for Classic HFS creation. The Master Directory Block
  /// stores a Pascal-string volume name at <c>drVN</c> (offset 36, max 27
  /// bytes) — the classic Mac Finder surfaces this as the disk's name.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 27),
  ];

  /// <summary>
  /// Walks the HFS catalog B-tree leaf chain and yields the actual on-disk
  /// byte layout — boot blocks + MDB + volume bitmap + catalog file as
  /// <see cref="DefragBlockKind.MetadataReserved"/>, every file record's
  /// data-fork extent (filExtRec[0]) as <see cref="DefragBlockKind.Used"/>.
  /// Coverage matches what <see cref="HfsReader"/> can extract — first leaf
  /// chain only, single data-fork extent per file.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => HfsExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in the HFS image: free allocation blocks, gaps
  /// between files and the block-tip slack between a file's logical size and
  /// the end of its last allocated 512-byte block. The catalog extent map
  /// clamps each file's run to its logical byte length, so trailing slack
  /// inside the final block presents as a free gap that the generic
  /// <see cref="UnusedSpaceWiper"/> zero-fills.
  ///
  /// <para>The HFS extent map keys each <see cref="DefragBlockInfo.FileName"/>
  /// by the catalog <em>leaf</em> name, whereas <see cref="HfsReader"/> reports
  /// the full slash-separated path; the size lookup is therefore keyed by the
  /// leaf segment so the explicit cluster-tip pass matches.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new HfsReader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory) {
            var leaf = LeafName(entry.Name);
            sizeMap[leaf] = entry.Size;
          }
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = HfsExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // The catalog extent map labels file extents with the leaf name only.
  private static string LeafName(string path) {
    var slash = path.LastIndexOf('/');
    return slash < 0 ? path : path[(slash + 1)..];
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new HfsBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new HfsBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public string Id => "Hfs";
  public string DisplayName => "HFS (Classic)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Hfs image via
  /// <see cref="HfsModifier.AddFile"/>. The modifier mutates the catalog leaf,
  /// volume bitmap, MDB, and alternate MDB in place; on leaf overflow it
  /// transparently falls back to a writer-driven rebuild so the call always
  /// succeeds.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FlatFiles(inputs))
      HfsModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing Hfs image via
  /// <see cref="HfsModifier.RemoveFile"/>. File data blocks are wiped and
  /// catalog records are excised from the leaf; missing names are silently
  /// ignored.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // The in-place modifier only handles the simple single-leaf catalog shape and
    // returns false otherwise — a silent no-op would leave the "removed" file (and
    // its data) intact. Collect anything it couldn't remove and fall back to a
    // verified clean rebuild so the removal (and forensic erasure) always happens.
    var unresolved = new List<string>();
    foreach (var name in entryNames)
      if (!HfsModifier.RemoveFile(archive, name, wipeData: true))
        unresolved.Add(name);
    if (unresolved.Count == 0) return;

    var skip = new HashSet<string>(unresolved, StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(rel) || skip.Contains(Path.GetFileName(rel)))
          File.Delete(file);
      }
    });
  }

  public string DefaultExtension => ".hfs";
  public IReadOnlyList<string> Extensions => [".hfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x42, 0x44], Offset: 1024, Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Classic Macintosh HFS filesystem image (pre-HFS+). Writer emits a
  /// spec-compliant MDB, volume bitmap, and real extents + catalog B-trees
  /// with thread records, file records, and a root-dir record — matching
  /// Inside Macintosh: Files (1992). Scope: flat root directory, ASCII
  /// filenames, ≤ ~30 files per image (single-leaf catalog).
  /// </summary>
  public string Description => "Classic Macintosh HFS filesystem image (pre-HFS+)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
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
    var r = new HfsReader(archive);
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
    var w = new HfsWriter();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label)) w.SetVolumeName(label);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <inheritdoc/>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware HFS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a contiguous,
  /// start-packed allocation block layout, so all four <see cref="DefragMode"/>
  /// values converge on a clean repack.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new HfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new HfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }
}
