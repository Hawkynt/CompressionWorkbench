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
/// Read-only. The whole local tree is listed from the Fossil root, so the live file system
/// is <c>active/…</c> and local snapshots, if any, are <c>snapshot/…</c>. Blocks already
/// archived to Venti are refused, because there is no Venti here to fetch them from.
/// </para>
/// <para>
/// Proof: volumes made by plan9port's <c>flfmt</c> and filled by its <c>fossil</c> server
/// over 9P read byte-identical to what that server returns. Sourcing: rung 1 — converted
/// from plan9port's MIT-licensed fossil sources (THIRD-PARTY-NOTICE.Fossil.txt). No writer:
/// a writer must keep the label partition, epochs and tags consistent, and is not yet
/// proven against <c>fossil</c>.
/// </para>
/// </remarks>
public sealed class FossilFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Fossil";
  public string DisplayName => "Fossil (Plan 9)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
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
  public string Description => "Plan 9 Fossil file server volume (local blocks); read-only, Venti-archived blocks refused.";

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
