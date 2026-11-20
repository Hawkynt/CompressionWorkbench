#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.UnixV6;

/// <summary>
/// Sixth Edition UNIX file system (PDP-11, 1975).
/// <para>
/// The volume has no magic number, so it is found by its <c>.v6</c> extension or
/// selected by id rather than sniffed.
/// The reader is checked against the 1975 root volume Dennis Ritchie's tapes
/// preserve (TUHS), whose files were independently extracted to a tar archive.
/// Written volumes are checked by the V6 system itself, running on a simulated
/// PDP-11: <c>icheck</c> and <c>dcheck</c> pass, the kernel mounts the volume, reads
/// every file back unchanged and writes into it.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>V6 <c>/usr/sys/filsys.h</c>, <c>inode.h</c>, <c>ken/subr.c</c> (The Unix Heritage Society archive)</description></item>
///   <item><description>John Lions, <i>A Commentary on the Sixth Edition UNIX Operating System</i> (1977)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Version_6_Unix</c></description></item>
/// </list>
/// </summary>
public sealed class UnixV6FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "UnixV6";
  public string DisplayName => "UNIX V6 file system";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
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

  /// <summary>
  /// Writes a volume with directories 0755 and files 0644 owned by root, each input's
  /// modification time as its access and modification time.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var root = new Folder();
    foreach (var input in inputs) {
      var parts = input.ArchiveName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;
      var folder = root;
      for (var i = 0; i < parts.Length - 1; ++i) folder = folder.Sub(parts[i]);
      var time = Seconds(input);
      if (input.IsDirectory) {
        folder.Sub(parts[^1]).Time ??= time;
        continue;
      }
      if (folder.Files.Exists(f => f.Name == parts[^1]))
        throw new ArgumentException($"'{input.ArchiveName}' is added twice.", nameof(inputs));
      folder.Files.Add(new(parts[^1], input.ReadContent()) { Time = time });
    }
    UnixV6Writer.Write(output, root.Node(""), time: root.Time ?? 0);
  }

  private static uint Seconds(ArchiveInputInfo input) {
    var time = input.Metadata?.LastWriteTimeUtc?.UtcDateTime
      ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTimeUtc(input.FullPath) : DateTime.UtcNow);
    var seconds = (time - DateTime.UnixEpoch).TotalSeconds;
    return seconds <= 0 ? 0 : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }

  private sealed class Folder {
    public uint? Time { get; set; }
    public List<(string Name, Folder Folder)> Folders { get; } = [];
    public List<UnixV6WriteNode> Files { get; } = [];

    public Folder Sub(string name) {
      foreach (var (n, f) in this.Folders)
        if (n == name) return f;
      var folder = new Folder();
      this.Folders.Add((name, folder));
      return folder;
    }

    public UnixV6WriteNode Node(string name) =>
      new(name, Children: [.. this.Folders.Select(f => f.Folder.Node(f.Name)), .. this.Files]) { Time = this.Time ?? 0 };
  }
}
