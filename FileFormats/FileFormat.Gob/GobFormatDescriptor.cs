#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Gob;

/// <summary>
/// LucasArts GOB resource archive used by Star Wars: Jedi Knight (Dark Forces II) and Outlaws.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/luciusDXL/TheForceEngine</c> — The Force Engine — maintained open reimplementation of the Jedi engine, reads GOB containers</description></item>
///   <item><description>No official specification — community-reverse-engineered LucasArts container</description></item>
/// </list>
/// </summary>
public sealed class GobFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new GobReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "Gob";
  public string DisplayName => "Lucasarts GOB";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gob";
  public IReadOnlyList<string> Extensions => [".gob", ".goo"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Trailing space is part of the GOB v2 magic — without it we would collide with
  // GOB v1 (Dark Forces) which is structurally different and out of scope here.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("GOB "u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("gob2", "GOB v2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Lucasarts archive (Jedi Knight, Outlaws)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new GobReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new GobReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new GobWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddEntry(name, data);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new GobReader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new GobWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
        }
        return ms.ToArray();
      });
  }
}
