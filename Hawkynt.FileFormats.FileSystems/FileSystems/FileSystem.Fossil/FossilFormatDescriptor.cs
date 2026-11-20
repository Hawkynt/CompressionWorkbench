#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Fossil;

/// <summary>
/// Fossil — the Plan 9 file server's on-disk format (Quinlan, McKie, Cox, 2002), as plan9port's
/// <c>flfmt</c> and <c>fossil</c> write it. Header magic <c>0x3776ae89</c> at 128 KiB.
/// </summary>
/// <remarks>
/// <para>
/// The whole local tree is listed from the Fossil root, so the live file system is
/// <c>active/…</c> and local snapshots, if any, are <c>snapshot/…</c>. Blocks already archived
/// to Venti are refused, because there is no Venti here to fetch them from. Fresh volumes are
/// written by <see cref="FossilWriter"/>.
/// </para>
/// <para>
/// Proof: volumes made by plan9port's <c>flfmt</c> and filled by its <c>fossil</c> server over
/// 9P read byte-identical to what that server returns; volumes we write are served by that
/// server with the same files, sizes and modes, and it can add files to them. Sourcing: rung 1
/// (<see cref="FossilReader"/> and <see cref="FossilWriter"/> name their plan9port sources).
/// </para>
/// </remarks>
public sealed class FossilFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Fossil";
  public string DisplayName => "Fossil (Plan 9)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".fossil";
  public IReadOnlyList<string> Extensions => [".fossil"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x37, 0x76, 0xAE, 0x89, 0x00, 0x01], Offset: FossilReader.HeaderOffset, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Plan 9 Fossil file server volume (local blocks); Venti-archived blocks refused; fresh volumes written.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FossilReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, e.Size, "Stored", e.IsDirectory, false,
      DateTime.UnixEpoch.AddSeconds(e.MTime))).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FossilReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, e.Path.Replace('/', Path.DirectorySeparatorChar)));
        continue;
      }
      WriteFile(outputDir, e.Path, r.Extract(e));
    }
  }

  /// <summary>
  /// Writes a volume (8 KiB blocks) whose <c>active</c> tree holds the inputs: directories 0775,
  /// files 0664, owner <c>adm</c>, each input's modification time. A leading <c>active/</c> in the
  /// names is taken as the tree's root, so a volume listed by this descriptor writes back alike.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var root = new Folder();
    var stripActive = inputs.Count > 0 && inputs.All(i => {
      var n = i.ArchiveName.Replace('\\', '/').TrimStart('/');
      return n == "active" || n == "active/" || n.StartsWith("active/", StringComparison.Ordinal);
    });
    var now = Seconds(DateTime.UtcNow);
    foreach (var input in inputs) {
      var parts = input.ArchiveName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
      if (stripActive) parts.RemoveAt(0);
      if (parts.Count == 0) continue;
      var folder = root;
      for (var i = 0; i < parts.Count - 1; ++i)
        folder = folder.Sub(parts[i], now);
      var time = Seconds(input.Metadata?.LastWriteTimeUtc?.UtcDateTime
        ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTimeUtc(input.FullPath) : DateTime.UtcNow));
      if (input.IsDirectory) {
        folder.Sub(parts[^1], time).Time = time;
        continue;
      }
      folder.Files.Add(new(parts[^1], false) { Data = input.ReadContent(), MTime = time });
    }
    FossilWriter.Write(output, root.Nodes(), time: now);
  }

  private static uint Seconds(DateTime time) {
    var seconds = (time - DateTime.UnixEpoch).TotalSeconds;
    return seconds <= 0 ? 0 : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }

  private sealed class Folder {
    public uint Time { get; set; }
    public List<(string Name, Folder Folder)> Folders { get; } = [];
    public List<FossilWriteNode> Files { get; } = [];

    public Folder Sub(string name, uint time) {
      foreach (var (n, f) in this.Folders)
        if (n == name) return f;
      var folder = new Folder { Time = time };
      this.Folders.Add((name, folder));
      return folder;
    }

    public List<FossilWriteNode> Nodes() => [
      .. this.Folders.Select(f => new FossilWriteNode(f.Name, true) { Permissions = 0x1FD, MTime = f.Folder.Time, Children = f.Folder.Nodes() }),
      .. this.Files,
    ];
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new FossilReader(archive);
    var name = entryName.Replace('\\', '/').TrimStart('/');
    var e = r.Entries.FirstOrDefault(x => !x.IsDirectory && x.Path == name)
      ?? throw new FileNotFoundException($"Fossil volume has no file '{entryName}'.", entryName);
    return r.Extract(e);
  }
}
