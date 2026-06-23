#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ReiserFs;

public sealed class ReiserFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The one tunable the writer honours: the volume label written into the
  /// superblock <c>s_label</c> field (16 bytes) via <see cref="ReiserFsWriter.Label"/>
  /// and read back as <c>ReiserFsReader.Label</c>. The 4&#160;KB block size and
  /// R5 hash are fixed by the v3.6 layout, so they are not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
  ];

  // R/W write constraints — ReiserFS has no inherent ceiling; real mkfs.reiserfs minimum ≈ 128 MB.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 128L * 1024 * 1024;
  public string AcceptedInputsDescription => "ReiserFS v3.6 filesystem image; full multi-leaf S+tree with nested directories and INDIRECT-item file bodies.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  public string Id => "ReiserFs";
  public string DisplayName => "ReiserFS";
  public FormatCategory Category => FormatCategory.Archive;
  // WORM, not R/W: Add/Remove rebuild the whole image (read-all -> re-create),
  // so the verb works via rebuild but nothing is modified in place. CanModify
  // must not be advertised. See Compression.Registry/FormatCapabilities.cs.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  public string DefaultExtension => ".reiserfs";
  public IReadOnlyList<string> Extensions => [".reiserfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ReIsErFs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
    new("ReIsEr2Fs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
    new("ReIsEr3Fs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// ReiserFS v3.6 filesystem image — R/W. The writer emits a real
  /// spec-compliant multi-leaf S+tree image (superblock at +65536, R5-hashed
  /// directory entries, INDIRECT items with dedicated data blocks for file
  /// bodies &gt; 1 KB, internal pages above leaves). In-place
  /// <see cref="IArchiveModifiable"/> is implemented via read-modify-rebuild:
  /// every existing entry is materialised, the requested edit is applied in
  /// memory, and a fresh image is written back to the stream. This covers
  /// nested paths, leaf splits and merges, multi-leaf descent, INDIRECT-sized
  /// bodies, and root tree-height growth — all paths that previously fell back
  /// to NotSupportedException. The cost is O(image size) per Add / Remove
  /// rather than O(edit-locality), but the result passes reiserfsck.
  /// </summary>
  public string Description => "ReiserFS v3 filesystem image (R/W, full S+tree mutation via rebuild)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ReiserFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ReiserFsReader(stream);
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
    var r = new ReiserFsReader(archive);
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
    var w = new ReiserFsWriter();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label))
      w.Label = label;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Two-pass streaming creation. ReiserFS v3.6 has NO block checksums by
  /// design, so file bodies are fully streamable. Bodies above the writer's
  /// DIRECT/tail threshold (1 KiB) become INDIRECT items backed by dedicated
  /// data blocks: pass 1 builds the S+tree with those data-block runs left zero;
  /// pass 2 seeks to each run and copies its bytes from
  /// <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// in 64 KiB chunks. Tail-packed bodies ≤ 1 KiB live inside shared leaves, so
  /// the writer reads those small bodies up front (a bounded read). The output
  /// is byte-identical to <see cref="Create"/> for the same inputs. Falls back
  /// to the buffered default on a non-seekable target.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new ReiserFsWriter();
    if (!output.CanSeek) {
      foreach (var input in inputs) {
        if (input.IsDirectory) continue;
        using var src = input.OpenStream();
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        w.AddFile(input.Name, ms.ToArray());
      }
      w.WriteTo(output);
      return;
    }
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    w.BuildToStreaming(output);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ReiserFS v3.6 defragmentor via read-extract-rebuild dispatch
  /// through <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start single-leaf image (superblock at +65536, root SD
  /// + DIRENTRY + per-file SD/DIRECT items, R5-hashed key ordering).
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadEntries, BuildImage);

  // ── IArchiveModifiable ────────────────────────────────────────────────
  // Read-modify-rebuild via the multi-leaf writer. Every Add and Remove
  // materialises the live entries from the current image, applies the edit
  // in memory, and rewrites the image. Covers nested paths, leaf splits and
  // merges, multi-leaf descent, INDIRECT-sized bodies and root tree-height
  // growth — every path that previously fell back to NotSupportedException.

  /// <summary>
  /// Adds (or replaces, on name collision) the given files inside an existing
  /// ReiserFS image. Routed through <see cref="ReiserFsModifier"/> which does
  /// the full read-modify-rebuild via <see cref="ReiserFsWriter"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      ReiserFsModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing ReiserFS image. The rebuild
  /// always starts from zeroed bytes so the removed file data leaves no
  /// forensic trace.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ReiserFsModifier.RemoveFile(archive, name, wipeData: true);
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new ReiserFsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new ReiserFsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }
}
