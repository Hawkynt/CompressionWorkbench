#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Nilfs2;

/// <summary>
/// NILFS2 descriptor (continuous-snapshot log-structured filesystem, Linux mainline
/// since 2.6.30). Magic 0x3434 sits at superblock+6 (file offset 1030).
///
/// <para><b>R/W scope.</b> Create emits a spec-compliant superblock plus a
/// writer-private compact directory at offset 2048 (the base checkpoint at
/// cno=1). Add / Replace / Remove append a fresh log segment ("NILFS2SG"
/// header + cno + dirents + payload) at the tail of the volume and bump
/// <c>s_last_cno</c> in the superblock — the only in-place edit, sanctioned by
/// the NILFS2 spec for advancing the checkpoint pointer. Every byte of every
/// prior segment stays byte-identical at its original offset, so the older
/// state is byte-recoverable as a snapshot (continuous-snapshot semantic).</para>
///
/// <para><b>Honest scope — what's NOT done.</b> Full kernel-grade DAT
/// (Disk Address Translation) B-tree, IFile / CPFile / SUFile metadata files,
/// segment-summary CRCs and segment-log replay are multi-week and remain out
/// of scope. A real <c>mount -t nilfs2</c> rejects the image. What's load-bearing
/// here is the spec-canonical "append new segment + bump last_cno" mutation
/// semantic + the byte-identical-old-segment preservation, which round-trips
/// through this descriptor's reader.</para>
/// </summary>
public sealed class Nilfs2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable {
  public string Id => "Nilfs2";
  public string DisplayName => "NILFS2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".nilfs2";
  public IReadOnlyList<string> Extensions => [".nilfs2", ".nilfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // NILFS_SUPER_MAGIC == 0x3434, little-endian at superblock+6 == file offset 1030.
    new([0x34, 0x34], Offset: 1030, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "NILFS2 continuous-snapshot log-structured filesystem — Create emits superblock + base directory; Add/Replace/Remove append a fresh log segment at the tail and bump s_last_cno (only in-place edit, spec-sanctioned). Prior segments stay byte-identical at original offsets (continuous-snapshot invariant). Full DAT/IFile/CPFile/SUFile + segment-log replay out of scope — not kernel-mountable.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Nilfs2Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Nilfs2Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Emits a self-contained NILFS2 image (valid superblock + base private
  /// directory at cno=1). Round-trips through this descriptor's reader and
  /// serves as the substrate for in-place Add / Replace / Remove via
  /// <see cref="Nilfs2InPlaceModifier"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var writer = new Nilfs2Writer();
    foreach (var (name, data) in FilesOnly(inputs))
      writer.AddFile(name, data);
    writer.WriteTo(output);
  }

  // ── IArchiveModifiable ────────────────────────────────────────────────

  /// <summary>
  /// Appends a fresh log segment at the tail of the image carrying dirent +
  /// data blocks for each input, and bumps <c>s_last_cno</c> in the superblock.
  /// The 8-byte cno field is the only in-place edit; every other byte of the
  /// prior image stays byte-identical at its original offset — continuous
  /// snapshot semantic intact. Inputs whose name already exists are
  /// effectively replaced (the higher cno wins on read).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    Nilfs2InPlaceModifier.Add(archive, inputs);
  }

  /// <summary>
  /// Appends a tombstone dirent for each named entry in a fresh log segment and
  /// bumps <c>s_last_cno</c>. The reader's cno-merge drops the entry from the
  /// listing; the original data blocks stay byte-identical at their original
  /// offsets and remain addressable as a snapshot of the pre-Remove state.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    Nilfs2InPlaceModifier.Remove(archive, entryNames);
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("Nilfs2 R/W is log-structured (append-only segments) — defragmentation would re-pack snapshots, which violates the continuous-snapshot invariant.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Nilfs2 R/W is log-structured (append-only segments) — defragmentation would re-pack snapshots, which violates the continuous-snapshot invariant.");
}
