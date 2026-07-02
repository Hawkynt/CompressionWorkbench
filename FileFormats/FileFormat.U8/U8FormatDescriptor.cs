#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.U8;

/// <summary>
/// Nintendo U8 archive (Wii / Wii U / 3DS) — node table + string pool + aligned file data.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://wiibrew.org/wiki/U8_archive</c> — WiiBrew wiki — community U8 archive documentation</description></item>
///   <item><description>Wiimms SZS Tools (wszst) — maintained implementation</description></item>
/// </list>
/// </summary>
public sealed class U8FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the U8 archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the U8 archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new U8Reader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new U8Writer(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
        }
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new U8Reader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "U8";
  public string DisplayName => "Nintendo U8";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".u8";
  public IReadOnlyList<string> Extensions => [".u8", ".arc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(new byte[] { 0x55, 0xAA, 0x38, 0x2D }, Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("u8", "U8")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Nintendo U8 archive (Wii / Wii U / 3DS / Switch)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new U8Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new U8Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory)
        continue;
      if (files != null && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new U8Writer(output, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var data = input.ReadContent();
      w.AddEntry(input.ArchiveName, data);
    }
  }
}
