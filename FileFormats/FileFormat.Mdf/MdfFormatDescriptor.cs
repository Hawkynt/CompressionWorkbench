#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mdf;

public sealed class MdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "MDF/MDS is a raw CD-ROM sector image (Alcohol 120%) — defragmentation isn't meaningful.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  public string Id => "Mdf";
  public string DisplayName => "MDF/MDS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".mdf";
  public IReadOnlyList<string> Extensions => [".mdf", ".mds"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // MDF has no file-header magic; it is raw sector data.
  // Detection relies on the ISO 9660 PVD heuristic (CD001 at LBA 16).
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Alcohol 120% MDF/MDS disc image (R/W via in-place sector rewrite at fixed offsets; inner ISO 9660 directory mutation delegated to FileSystem.Iso; multi-track .mds layouts deferred — the modifier mutates the MDF data only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MdfReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "iso9660", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MdfReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: emit plain 2048-byte ISO 9660 sectors. The reader's geometry detection
    // recognises this. The accompanying .MDS metadata sidecar isn't produced (the
    // Create API is single-stream); MDS isn't required to extract MDF content.
    var iso = new FileSystem.Iso.IsoWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      iso.AddFile(name, data);
    output.Write(iso.Build());
  }

  // ── IArchiveModifiable ──────────────────────────────────────────────

  /// <summary>
  /// Rewrites raw CD sectors in place. Inputs whose <c>ArchiveName</c> matches
  /// <c>sector-NNNNNN.bin</c> are written at the fixed byte offset
  /// <c>lba * sectorSize + dataOffset</c>; everything outside the touched
  /// 2 048-byte user-data region stays byte-identical.
  ///
  /// <para>Inputs not matching the synthetic sector schema are skipped —
  /// inner-ISO 9660 directory mutation is delegated to <c>FileSystem.Iso</c>
  /// and is out of scope for the sector-rewrite modifier. The accompanying
  /// <c>.mds</c> sidecar (if any) is not touched; the modifier only mutates
  /// the MDF byte stream.</para>
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    MdfInPlaceModifier.AddOrReplaceSectors(archive,
      inputs.Where(i => !i.IsDirectory).Select(i => (i.ArchiveName, i.ReadContent())));
  }

  /// <summary>
  /// Zeros the 2 048-byte user-data region of each named sector. Sector
  /// framing bytes (sync / address / mode / EDC) on raw geometries are
  /// preserved so the LBA-to-offset map and the rest of the image remain
  /// byte-identical.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    MdfInPlaceModifier.RemoveSectors(archive, entryNames);
  }
}
