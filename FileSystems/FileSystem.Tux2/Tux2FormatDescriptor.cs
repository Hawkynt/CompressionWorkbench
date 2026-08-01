#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux2;

/// <summary>
/// Read+WORM descriptor for TUX2 — Daniel Phillips's 2002 phase-tree
/// filesystem proposal (OLS 2002 paper, never-stabilised research format).
/// Recognises a deterministic header pattern (magic "TUX2FS\0\0" at offset 0)
/// so research images we generate round-trip through the reader. Writer emits
/// a single-phase image only (no alpha/beta phases, no version chain) — real
/// legacy prototype images would need a custom parser matching the specific
/// snapshot of the in-progress code that produced them.
///
/// References:
/// <list type="bullet">
///   <item><description>Daniel Phillips, "The Tux2 Filesystem" (Ottawa Linux Symposium 2002 proceedings) — the defining paper</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Tux3</c> — Wikipedia article covering the phase-tree lineage</description></item>
/// </list>
/// </summary>
public sealed class Tux2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  // ── Synthetic, non-file entries the reader always surfaces ──────────────
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.tux2", "metadata.ini" };

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The single tunable the single-phase WORM writer honours: the on-disk
  /// format version stamped into the header at offset 0x08. <see cref="Tux2Writer.Version"/>
  /// is written verbatim and <see cref="Tux2Reader.Version"/> reads it back, so
  /// the knob round-trips. Defaults to 1 (the version the reader documents).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Version", DisplayName: "Image version", Kind: FormatOptionKind.Integer, Default: "1",
      Description: "Format version stamped into the TUX2 header at offset 0x08."),
  ];

  public string Id => "Tux2";
  public string DisplayName => "TUX2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tux2";
  public IReadOnlyList<string> Extensions => [".tux2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("TUX2FS\0\0"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "TUX2 phase-tree research filesystem (Daniel Phillips, OLS 2002) — single-phase synthetic image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Tux2Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Tux2Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
    }
  }

  /// <summary>
  /// Emits a fresh single-phase TUX2 image: 16-byte header (magic + version +
  /// file count) followed by per-file records (u16 name length, UTF-8 name,
  /// u32 data length, raw bytes). Round-trips through <see cref="Tux2Reader"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var version = (uint)Math.Max(0, options.GetOptionInt("Version", 1));
    var w = new Tux2Writer { Version = version };
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Rewrites the image with every file laid out contiguously from the start.
  /// Records are copied straight from the old image into the new one, so a
  /// multi-gigabyte volume never has to fit in memory — the previous refusal
  /// was for want of wiring it up, not for want of a writer.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    // Every consolidate mode lands on the same layout here: the writer emits a
    // fresh volume packed from the first data block, and has no way to place
    // files against the tail. Carving a hole is the one request it cannot meet.
    if (options.Mode is DefragMode.CarveHole)
      throw new NotSupportedException(
        "Tux2 defragmentation cannot carve a hole: the rebuild always start-packs the volume.");

    var tempPath = Path.GetTempFileName();
    try {
      using (var temp = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite)) {
        using (var reader = new Tux2Reader(archive)) {
          var w = new Tux2Writer();
          foreach (var entry in reader.Entries) {
            if (entry.IsDirectory || SyntheticNames.Contains(entry.Name)) continue;
            var e = entry;
            w.AddStreamingFile(e.Name, e.Size, s => reader.ExtractTo(e, s));
          }
          w.WriteTo(temp);
        }

        options.OnProgress?.Invoke(new DefragProgressEvent(
          Phase: "commit", Fraction: 1.0, CurrentReadOffset: archive.Length,
          CurrentWriteOffset: temp.Length, ImageSize: temp.Length, BlockMap: null));

        temp.Position = 0;
        archive.Position = 0;
        temp.CopyTo(archive);
        archive.SetLength(temp.Length);
        archive.Flush();
      }
    } finally {
      File.Delete(tempPath);
    }
  }

  // ── IArchiveModifiable (genuine in-place R/W) ───────────────────────────
  //
  // Tux2InPlaceModifier appends/overwrites inline records, leaving the header
  // and all preceding records byte-identical at their original offsets. New
  // entries are append-only; same-size replaces overwrite data in place;
  // resize/delete tail-rewrite from the changed record onward (still O(tail)).
  // The rebuild fallback only fires on a malformed image.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this, SyntheticNames);
      return;
    }

    Tux2InPlaceModifier.Add(archive, inputs,
      (a, i) => ModifyRebuilder.Add(a, i, ReadEntries, BuildImage, largeVolumeCreator: this));
  }

  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this, SyntheticNames);
      return;
    }

    Tux2InPlaceModifier.Remove(archive, entryNames,
      (a, n) => ModifyRebuilder.Remove(a, n, ReadEntries, BuildImage, largeVolumeCreator: this));
  }

  // ── Shared rebuild delegates (exclude the reader's synthetic entries) ────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Tux2InPlaceModifier.ReadRealEntries(ms.ToArray()).ToList();
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new Tux2Writer();
    foreach (var (n, d) in files)
      if (!SyntheticNames.Contains(n))
        w.AddFile(n, d);
    return w.Build();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>Header, per-record prefixes and any tail slack are metadata; each record body is the file that owns it.</summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new Tux2Reader(image);
      var cursor = 0L;
      foreach (var e in reader.Entries
                 .Where(x => x.Offset >= 0 && x.Size > 0 && !SyntheticNames.Contains(x.Name))
                 .OrderBy(x => x.Offset)) {
        if (e.Offset > cursor)
          result.Add(new DefragBlockInfo(cursor, e.Offset - cursor, DefragBlockKind.MetadataReserved));
        result.Add(new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, e.Name));
        cursor = Math.Max(cursor, e.Offset + e.Size);
      }
      if (cursor == 0 && image.Length > 0)
        result.Add(new DefragBlockInfo(0, Math.Min(4096, image.Length), DefragBlockKind.MetadataReserved));
    } catch {
      // An image we cannot parse claims nothing, and a wipe of it would zero
      // every byte — so say it has no known extents and let the caller decide.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    // Records are packed to the byte, so there are no cluster tips to trim —
    // only the slack a removal or a shorter replacement left behind.
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }


  /// <summary>
  /// Re-lays the volume out with the requested geometry. The generic default
  /// would feed this reader's synthetic entries — the raw image and the
  /// metadata sheet — back in as files; they are excluded so the rebuilt
  /// volume holds the same files the original did.
  /// </summary>
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);

    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
    if (options.Parameters != null)
      foreach (var kv in options.Parameters)
        parameters[kv.Key] = kv.Value;

    RebuildVerb.RebuildToStream(source, target, this, this,
      parameters.Count > 0 ? parameters : null, SyntheticNames);
  }

}
