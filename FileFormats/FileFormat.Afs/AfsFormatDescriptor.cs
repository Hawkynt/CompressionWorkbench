#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Afs;

/// <summary>
/// Sega AFS archive ("AFS\0" magic) — audio/data container used by Dreamcast, PS2 and GameCube era titles.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/MaikelChan/AFSPacker</c> — AFSPacker — open-source AFS extractor/creator, the de-facto format reference</description></item>
///   <item><description>The container was never documented by Sega/CRI; the offset-table layout was recovered by the game-modding community</description></item>
/// </list>
/// </summary>
public sealed class AfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new AfsReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "Afs";
  public string DisplayName => "Sega AFS";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".afs";
  public IReadOnlyList<string> Extensions => [".afs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(new byte[] { 0x41, 0x46, 0x53, 0x00 }, Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("afs", "AFS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Sega Athena File System (Dreamcast, PS2, GameCube)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AfsReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AfsReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new AfsWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddEntry(name, data);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new AfsReader(stream, leaveOpen: true);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new AfsWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
        }
        return ms.ToArray();
      });
  }
}
