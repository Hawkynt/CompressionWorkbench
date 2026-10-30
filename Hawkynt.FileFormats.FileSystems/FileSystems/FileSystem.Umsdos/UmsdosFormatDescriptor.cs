#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Umsdos;

/// <summary>
/// UMSDOS — Linux on top of an MS-DOS FAT volume (Linux 1.1–2.6.10), read-only.
/// <para>
/// The volume is a FAT volume, so the FAT descriptor claims it by content; this view is
/// selected by id and shows what Linux saw: long names, modes, owners, symbolic links and
/// pseudo hard links from the per-directory <c>--linux-.---</c> files. Read-only:
/// no maintained kernel mounts UMSDOS, so a written volume could not be checked.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>Linux 2.4 <c>include/linux/umsdos_fs.h</c>, <c>fs/umsdos/mangle.c</c>, <c>fs/umsdos/dir.c</c> (read as specification)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/UMSDOS</c></description></item>
/// </list>
/// </summary>
public sealed class UmsdosFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Umsdos";
  public string DisplayName => "UMSDOS (Linux on FAT)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".img";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "UMSDOS: the Linux view of a FAT volume through its --linux-.--- files";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new UmsdosReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified,
      Kind: null, IsSymlink: e.IsSymlink, LinkTarget: e.LinkTarget)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new UmsdosReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.Name));
      else WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
