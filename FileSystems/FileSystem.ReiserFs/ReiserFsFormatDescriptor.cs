#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ReiserFs;

public sealed class ReiserFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable {
  // WORM write constraints — ReiserFS has no inherent ceiling; real mkfs.reiserfs minimum ≈ 128 MB.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 128L * 1024 * 1024;
  public string AcceptedInputsDescription => "ReiserFS v3.6 filesystem image; flat root directory, single leaf node.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  public string Id => "ReiserFs";
  public string DisplayName => "ReiserFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

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
  /// ReiserFS v3.6 filesystem image — WORM. The writer emits a real
  /// spec-compliant single-leaf image (superblock at +65536, root SD +
  /// DIRENTRY + per-file SD/DIRECT items, R5-hashed key ordering). True
  /// in-flight Add/Remove would require S+tree split/merge with
  /// comp_keys-ordered insertion, bitmap chain updates, and objectid map
  /// maintenance — multi-week work. Per project policy, WORM = create only;
  /// no in-flight modification.
  /// </summary>
  public string Description => "ReiserFS v3 filesystem image (WORM)";

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
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
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

  // ── IArchiveModifiable (rebuild-based add / replace / remove) ──────────
  // ReiserFS in-place S+tree mutation needs node split/merge + bitmap/objectid
  // bookkeeping; instead we read every file, rebuild a fresh image with the
  // (large-directory-capable, reiserfsck-clean) writer, and write it back —
  // the same read-extract-rebuild path the defragmentor uses.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs, ReadEntries, BuildImage);

  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames, ReadEntries, BuildImage);

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
