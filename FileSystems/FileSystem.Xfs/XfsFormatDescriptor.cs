#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Xfs;

public sealed class XfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveModifiable {
  // WORM write constraints — XFS has no inherent ceiling; real mkfs.xfs minimum ≈ 16 MB.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 16 * 1024 * 1024;
  public string AcceptedInputsDescription => "XFS v4 filesystem image; flat root directory, short-form entries.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  public string Id => "Xfs";
  public string DisplayName => "XFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".xfs";
  public IReadOnlyList<string> Extensions => [".xfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("XFSB"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "XFS filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new XfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new XfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new XfsWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Rebuild-style add/replace (see <see cref="XfsModifier"/>). Emits a fresh
  /// <c>xfs_repair -n -f</c>-clean image over the old bytes.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var toAdd = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (i.ArchiveName, File.ReadAllBytes(i.FullPath)))
      .ToList();
    XfsModifier.AddOrReplace(archive, toAdd);
  }

  /// <summary>
  /// Rebuild-style remove (see <see cref="XfsModifier"/>). The removed file's
  /// data does not survive into the rebuilt image because the new writer emits
  /// a fresh superblock, AGF/AGI, and inode table.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    XfsModifier.Remove(archive, entryNames);
  }
}
