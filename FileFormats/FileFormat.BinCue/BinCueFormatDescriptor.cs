#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.BinCue;

/// <summary>
/// BIN/CUE CD-ROM image — raw 2352-byte sector dump (.bin) described by a CDRWIN cue sheet (.cue).
///
/// References:
/// <list type="bullet">
///   <item><description>Golden Hawk Technology CDRWIN user manual — the defining cue-sheet documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Cue_sheet_(computing)</c> — cue-sheet syntax overview</description></item>
///   <item><description>ECMA-130 — CD-ROM sector layout (mode 1 / mode 2 framing)</description></item>
/// </list>
/// </summary>
public sealed class BinCueFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "BIN/CUE is a raw CD-ROM sector image — defragmentation isn't meaningful for a single ISO 9660 track.");
  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BinCue";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "BIN/CUE";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".bin";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".bin", ".cue"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // No reliable file-header magic: the BIN file is raw sector data and the
  // CUE file is plain text; detection relies on extension.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
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
public string Description => "BIN/CUE CD-ROM disc image";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new BinCueReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "iso9660", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new BinCueReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: emit the BIN as plain 2048-byte ISO 9660 cooked sectors. The reader
    // auto-detects this geometry. CUE sheet generation is not produced here -- the
    // Create API only gives us a single output stream; users wanting a CUE can
    // generate one trivially since a single Mode 1 data track is the default.
    var iso = new FileSystem.Iso.IsoWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      iso.AddFile(name, data);
    output.Write(iso.Build());
  }

  // ── IArchiveModifiable ──────────────────────────────────────────────

  /// <summary>
  /// Rewrites raw CD sectors in place. Inputs whose <c>ArchiveName</c>
  /// matches <c>sector-NNNNNN.bin</c> are written at the fixed byte offset
  /// <c>lba * sectorSize + dataOffset</c>; everything outside the touched
  /// 2 048-byte user-data region stays byte-identical.
  ///
  /// <para>Inputs not matching the synthetic sector schema are skipped —
  /// inner-ISO 9660 directory mutation is delegated to <c>FileSystem.Iso</c>
  /// and is out of scope for the sector-rewrite modifier.</para>
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    BinCueInPlaceModifier.AddOrReplaceSectors(archive,
      inputs.Where(i => !i.IsDirectory).Select(i => (i.ArchiveName, i.ReadContent())));
  }

  /// <summary>
  /// Zeros the 2 048-byte user-data region of each named sector. The
  /// sector framing bytes (sync / address / mode / EDC) on raw geometries
  /// are preserved so the LBA-to-offset map and the rest of the image
  /// remain byte-identical.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    BinCueInPlaceModifier.RemoveSectors(archive, entryNames);
  }

  // ── IArchiveLayoutMap ───────────────────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => BinCueLayoutMap.Enumerate(archive);
}
