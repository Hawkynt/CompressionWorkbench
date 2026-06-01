#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Jfs;

/// <summary>
/// Descriptor for IBM JFS1 aggregate images. Reader walks the kernel-fixed AIT
/// (block 11), the indirect fileset AIM → IAG → FSIT path, and the inline
/// dtree root + xtree extents. Writer emits a complete WORM image with
/// FILESYSTEM_I → AIM → IAG → FSIT, dual superblocks, dmap+dmapctl with
/// canonical <c>ujfs_adjtree</c> buddy tree, both AIT/AIM copies, and an
/// inline-dtroot root directory with up to 8 user files. Validated clean
/// against real <c>fsck.jfs -n -f -v</c>.
/// <para>
/// State: <b>WORM</b>. R/W in-place mutation deliberately not implemented —
/// adding files outside the inline 8-slot dtroot requires real dtree B+ tree
/// node split/balance, file growth past 288 B requires xtree B+ tree splits,
/// and every block alloc/free must rerun <c>ujfs_adjtree</c> across the
/// dmaptree+dmapctl. The capability surface is locked at WORM by
/// <c>JfsTests.Descriptor_IsHonestlyWormOnly</c> until that work lands;
/// see <c>SfsFormatDescriptor</c> / <c>BcacheFsFormatDescriptor</c> for the
/// same honest-WORM precedent.
/// </para>
/// </summary>
public sealed class JfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
                                          IArchiveCreatable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable {
  // WORM write constraints.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 16L * 1024 * 1024;
  public string AcceptedInputsDescription =>
    "JFS1 filesystem image; single allocation group, inline-dtree root, up to 8 files.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (!input.IsDirectory) {
      var leaf = Path.GetFileName(input.ArchiveName);
      if (leaf.Length > 11) {
        reason = "JFS writer supports inline-dtree slots only; file names must be ≤ 11 chars.";
        return false;
      }
    }
    reason = null;
    return true;
  }

  public string Id => "Jfs";
  public string DisplayName => "JFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".jfs";
  public IReadOnlyList<string> Extensions => [".jfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("JFS1"u8.ToArray(), Offset: 32768, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "IBM Journaled File System image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new JfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new JfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new JfsWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware JFS1 defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start single-aggregate image with FILESYSTEM_I → AIM →
  /// IAG → FSIT, dual superblocks, dmap+dmapctl, and an inline-dtroot.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadEntries, BuildImage);

  // ── IArchiveModifiable (rebuild-based add / replace / remove) ──────────
  // True in-place JFS mutation needs dmap/IAG/dtree updates; instead we read
  // every file and rebuild a fresh fsck.jfs-clean image with the writer (which
  // supports nested + large directories), the same path the defragmentor uses.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs, ReadEntries, BuildImage);

  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames, ReadEntries, BuildImage);

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new JfsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }
}
