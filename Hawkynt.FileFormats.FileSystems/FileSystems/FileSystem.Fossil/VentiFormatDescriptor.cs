#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Fossil;

/// <summary>
/// Venti — Plan 9's archival block store (Quinlan and Dorward, 2002) — as an arena partition that
/// plan9port's <c>fmtarenas</c> formats and its <c>venti</c> server fills. Header magic
/// <c>0xa9e4a5e7</c> at 256 KiB.
/// </summary>
/// <remarks>
/// <para>
/// Every vac root stored in the arenas (written by <c>vac</c>, or by a Fossil server archiving
/// a snapshot) is listed as a folder named by its score, holding the tree it names. The blocks
/// are found by scanning the arenas' clump logs; the index sections are not needed. Read-only:
/// Venti is append-only and its writer is the server.
/// </para>
/// <para>
/// Proof: arena partitions plan9port's <c>venti</c> filled from <c>vac</c> and from a Fossil
/// archive read to the same files and bytes that <c>unvac</c> and the Fossil server return.
/// Sourcing: rung 1 (<see cref="VentiArenaStore" /> names its plan9port sources).
/// </para>
/// </remarks>
public sealed class VentiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Venti";
  public string DisplayName => "Venti arena partition (Plan 9)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".arenas";
  public IReadOnlyList<string> Extensions => [".arenas"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xA9, 0xE4, 0xA5, 0xE7, 0x00, 0x00, 0x00, 0x03], Offset: VentiArenaStore.PartBlank, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored"), new("whack", "Whack")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Plan 9 Venti arena partition; every vac root's tree listed under its score (read-only).";

  /// <summary>Every vac tree in the partition, as (folder, reader); roots that are not vac trees are skipped.</summary>
  public static List<(string Folder, FossilReader Tree)> Trees(VentiArenaStore store) {
    var trees = new List<(string, FossilReader)>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var clump in store.Clumps.Where(static c => c.Type == VentiType.Root)) {
      var folder = Convert.ToHexStringLower(clump.Score);
      if (!seen.Add(folder)) continue;
      FossilReader tree;
      try {
        tree = FossilReader.OpenVac(store, clump.Score);
      } catch (InvalidDataException) {
        continue; // a root of another kind (not "vac"), or one whose blocks are not all here
      }
      trees.Add((folder, tree));
    }
    return trees;
  }

  private static IEnumerable<(string Path, FossilEntry? Entry, FossilReader? Tree)> Walk(Stream stream) {
    var store = new VentiArenaStore(stream);
    foreach (var (folder, tree) in Trees(store)) {
      yield return (folder, null, null);
      foreach (var e in tree.Entries) yield return (folder + "/" + e.Path, e, tree);
    }
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var list = new List<ArchiveEntryInfo>();
    foreach (var (path, e, _) in Walk(stream))
      list.Add(e == null
        ? new ArchiveEntryInfo(list.Count, path, 0, 0, "Stored", true, false, null)
        : new ArchiveEntryInfo(list.Count, path, e.Size, e.Size, "Stored", e.IsDirectory, false, DateTime.UnixEpoch.AddSeconds(e.MTime)));
    return list;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (path, e, tree) in Walk(stream)) {
      if (files != null && !MatchesFilter(path, files)) continue;
      if (e == null || e.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, path.Replace('/', Path.DirectorySeparatorChar)));
        continue;
      }
      WriteFile(outputDir, path, tree!.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var name = entryName.Replace('\\', '/').TrimStart('/');
    foreach (var (path, e, tree) in Walk(archive))
      if (e is { IsDirectory: false } && path == name) return tree!.Extract(e);
    throw new FileNotFoundException($"Venti arena partition has no file '{entryName}'.", entryName);
  }
}
