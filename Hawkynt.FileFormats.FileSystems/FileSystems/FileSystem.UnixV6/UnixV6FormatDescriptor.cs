#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.UnixV6;

/// <summary>
/// Sixth Edition UNIX file system (PDP-11, 1975), read-only.
/// <para>
/// The volume has no magic number, so it is found by its <c>.v6</c> extension or
/// selected by id rather than sniffed.
/// Read-only because nothing outside this package can check a V6 volume written
/// here: no maintained kernel or tool mounts the format. The reader is checked
/// against the 1975 root volume Dennis Ritchie's tapes preserve (TUHS), whose
/// files were independently extracted to a tar archive.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>V6 <c>/usr/sys/filsys.h</c>, <c>inode.h</c>, <c>ken/subr.c</c> (The Unix Heritage Society archive)</description></item>
///   <item><description>John Lions, <i>A Commentary on the Sixth Edition UNIX Operating System</i> (1977)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Version_6_Unix</c></description></item>
/// </list>
/// </summary>
public sealed class UnixV6FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "UnixV6";
  public string DisplayName => "UNIX V6 file system";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".v6";
  public IReadOnlyList<string> Extensions => [".v6"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Sixth Edition UNIX (PDP-11) file system image: 512-byte blocks, 14-character names";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new UnixV6Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new UnixV6Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.Name));
      else WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
