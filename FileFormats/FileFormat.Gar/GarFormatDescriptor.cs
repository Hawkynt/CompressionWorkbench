#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Gar;

/// <summary>
/// Nintendo 3DS GAR (Generic Asset Resource) archive as used in Tomodachi Life / Animal Crossing-era titles.
///
/// References:
/// <list type="bullet">
///   <item><description>No official specification — proprietary Nintendo-era container, reverse-engineered by the 3DS modding community</description></item>
///   <item><description><c>https://github.com/FanTranslatorsInternational/Kuriimu2</c> — Kuriimu2 — fan-translation toolkit covering many 3DS archive containers</description></item>
/// </list>
/// </summary>
public sealed class GarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new GarReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "Gar";
  public string DisplayName => "Nintendo 3DS GAR";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gar";
  public IReadOnlyList<string> Extensions => [".gar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(new byte[] { 0x47, 0x41, 0x52, 0x05 }, Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("gar-v5", "GAR v5")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Nintendo 3DS Generic Asset Resource (Tomodachi Life / Animal Crossing era)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new GarReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new GarReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new GarWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddEntry(name, data);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new GarReader(stream, leaveOpen: true);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new GarWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
        }
        return ms.ToArray();
      });
  }
}
