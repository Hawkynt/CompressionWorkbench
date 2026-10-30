#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.UnixV7;

/// <summary>
/// Seventh Edition UNIX file system (PDP-11, 1979): read, and created fresh (WORM).
/// <para>
/// The volume has no magic number, so it is never claimed by content sniffing:
/// it is found by its <c>.v7</c> extension or selected by id. Regular files and
/// directories are listed; device nodes carry no data and are skipped.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>V7 <c>sys/h/filsys.h</c>, <c>ino.h</c>, <c>dir.h</c>, <c>fblk.h</c> and <c>cmd/mkfs.c</c> (The Unix Heritage Society archive)</description></item>
///   <item><description>Linux <c>fs/sysv</c> (<c>v7</c> file-system type) — the oracle the writer is checked against</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Version_7_Unix</c></description></item>
/// </list>
/// </summary>
public sealed class UnixV7FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {

  public string Id => "UnixV7";
  public string DisplayName => "UNIX V7 file system";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".v7";
  public IReadOnlyList<string> Extensions => [".v7"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Seventh Edition UNIX (PDP-11) file system image: 512-byte blocks, 14-character names";

  public long? MaxTotalArchiveSize => (long)UnixV7Layout.MaxVolumeBlocks * UnixV7Layout.BlockSize;
  public long? MinTotalArchiveSize => null;
  public string AcceptedInputsDescription => "Files and directories with path components of 1–14 Latin-1 characters.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    foreach (var part in input.ArchiveName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
      if (UnixV7Writer.Reject(part) is { } why) { reason = why; return false; }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new UnixV7Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new UnixV7Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.Name));
      else WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new UnixV7Writer();
    foreach (var i in inputs) {
      if (i.IsDirectory) w.AddDirectory(i.ArchiveName, i.Metadata?.LastWriteTimeUtc?.UtcDateTime);
      else w.AddFile(i.ArchiveName, i.ReadContent(), i.Metadata?.LastWriteTimeUtc?.UtcDateTime ?? LastWrite(i));
    }
    output.Write(w.Build());
  }

  private static DateTime? LastWrite(ArchiveInputInfo input)
    => input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTimeUtc(input.FullPath) : null;
}
