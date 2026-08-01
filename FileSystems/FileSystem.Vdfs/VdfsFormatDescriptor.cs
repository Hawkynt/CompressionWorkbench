#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Vdfs;

/// <summary>
/// Descriptor for Gothic-engine VDFS archives (magic "PSVDSC_V2.00", .vdf) —
/// the virtual-disk container used by Piranha Bytes' ZenGin games.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://gothic-modding-community.github.io/gmc/</c> — Gothic Modding Community documentation</description></item>
///   <item><description><c>https://github.com/REGoth-project/REGoth</c> — REGoth engine reimplementation — includes a VDFS reader</description></item>
///   <item><description>VdfsSharp — C# VDFS extractor/creator (GitHub)</description></item>
/// </list>
/// </summary>
public sealed class VdfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty, ILayoutOptimizable {
  public string Id => "Vdfs";
  public string DisplayName => "VDFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vdf";
  public IReadOnlyList<string> Extensions => [".vdf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("PSVDSC_V2.00"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Gothic game engine VDFS archive (documented by REGoth wiki, Gothic Modding Community, and VdfsSharp)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new VdfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new VdfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Streamed, not buffered: an entry may be larger than a byte[] can hold.
      using var target = CreateEntryFile(outputDir, e.Name);
      r.ExtractTo(e, target);
    }
  }

  // ── IArchiveCreatable ──────────────────────────────────────────────────

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
    var r = new VdfsReader(archive);
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
    var w = new VdfsWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed for the entry table; reading a large input into
      // a byte[] would cap the container at what an array can hold.
      var name = Path.GetFileName(info.ArchiveName);
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    w.WriteTo(output);
  }

  // ── IArchiveModifiable ─────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces) files in a VDFS archive by appending the new data at
  /// end-of-stream and relocating the entry table past it — surviving file
  /// extents keep their original absolute byte offsets so their jump pointers
  /// stay valid without rewriting them. See <see cref="VdfsInPlaceModifier"/>
  /// for the full mutation strategy.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FlatFiles(inputs))
      VdfsInPlaceModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries by zeroing each entry record on disk (an empty
  /// first byte makes the reader skip it) and zero-wiping each removed file's
  /// data extent. Neighbour entry positions and live data extents are not
  /// disturbed. See <see cref="VdfsInPlaceModifier"/>.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      VdfsInPlaceModifier.RemoveFile(archive, name);
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // A container too large to materialise goes through the streaming rebuilder;
    // BuildImage returns a byte[] of the whole thing.
    // Every mode streams above the cap: end-pack and carve-hole order their
    // entries from scratch inside the rebuilder, so none of them falls back
    // to a buffered rebuild the volume is too large for.
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      VdfsWriter? streamWriter = null;
      Stream? target = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => ReadEntries(stream).ToList(),
        beginWrite: s2 => { streamWriter = new VdfsWriter(); target = s2; },
        // As a stream factory, not inline: an inline payload would go back into
        // the buffer this path exists to avoid.
        writeEntry: (name, data) => streamWriter!.AddStreamingFile(
          name, data.LongLength, () => new MemoryStream(data, writable: false)),
        finishWrite: () => streamWriter!.WriteTo(target!));
    }
  }

  /// <summary>Largest container a defrag will rebuild through a byte[].</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  // ── IFilesystemExtentMap ───────────────────────────────────────────────

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    var r = new VdfsReader(image);
    var entries = r.Entries;

    // VDFS is packed header → entry-table → file data. The entry table does NOT
    // start at a fixed offset (the reader honours the header's rootOffset) and can
    // extend well past a "36 + count*80" estimate, so anchoring the metadata region
    // on a guessed size let the wiper treat live directory entries as free space and
    // zero them (live-data loss). Reserve everything up to the FIRST file's data
    // instead — that provably covers the header, the whole entry table and any
    // padding, so only genuine trailing free space is ever wiped.
    var firstData = entries
      .Where(e => !e.IsDirectory && e.Size > 0)
      .Select(e => (long)e.DataOffset)
      .DefaultIfEmpty(image.Length)
      .Min();
    if (firstData > 0)
      yield return new DefragBlockInfo(0, firstData, DefragBlockKind.MetadataReserved, "header+entries");

    foreach (var e in entries) {
      if (e.IsDirectory || e.Size <= 0) continue;
      yield return new DefragBlockInfo(e.DataOffset, e.Size, DefragBlockKind.Used, e.Name);
    }
  }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros every byte of a VDFS container not claimed by the header/entry
  /// table or a live file extent — dead bytes left behind by editing or
  /// truncation. VDFS is a packed archive: each entry's data is contiguous and
  /// its extent length equals its logical size, so there is no cluster-tip
  /// slack to scrub (<paramref name="wipeClusterTips"/> has no effect here).
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = this.EnumerateExtents(image).ToList();

    // Packed archive — file extents already end exactly at the logical size, so
    // there are no cluster tips. Wipe free regions only.
    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  // ── Shared helpers ─────────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new VdfsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new VdfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }
}
