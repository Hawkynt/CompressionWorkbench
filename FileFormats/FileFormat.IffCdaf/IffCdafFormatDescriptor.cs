#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.IffCdaf;

/// <summary>
/// Amiga IFF CDAF (Compact Disk Archive Format) — an EA-IFF-85 FORM container carrying FNAM/FDAT chunk pairs per archived file.
///
/// References:
/// <list type="bullet">
///   <item><description>"EA IFF 85: Standard for Interchange Format Files" (Jerry Morrison, Electronic Arts, 1985) — the underlying container standard</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Interchange_File_Format</c> — Wikipedia on IFF</description></item>
///   <item><description><c>https://aminet.net</c> — Aminet — distribution home of the Amiga CDAF tooling</description></item>
/// </list>
/// </summary>
public sealed class IffCdafFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new IffCdafReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "IffCdaf";
  public string DisplayName => "IFF CDAF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an IFF-CDAF archive. Uses
  /// <see cref="IffCdafModifier"/> — appends FNAM+FDAT chunk pairs and
  /// updates the FORM header size.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      IffCdafModifier.RemoveFile(archive, name, wipeData: true);
      IffCdafModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="IffCdafModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      IffCdafModifier.RemoveFile(archive, name, wipeData: true);
  }
  public string DefaultExtension => ".cdaf";
  public IReadOnlyList<string> Extensions => [".cdaf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "IFF Compact Disk Archive Format (Amiga)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new IffCdafReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new IffCdafReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new IffCdafWriter();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new IffCdafReader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new IffCdafWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }
}
