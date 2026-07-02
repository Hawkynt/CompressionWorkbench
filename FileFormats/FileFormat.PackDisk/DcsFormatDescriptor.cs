#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PackDisk;

/// <summary>
/// Amiga DCS disk archive — whole-floppy track data compressed with the XPK library.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://aminet.net</c> — Aminet — distribution home of the archiver and the XPK compression library</description></item>
///   <item><description>XPK master library developer documentation (Amiga) — defines the XPKF container the track data is stored in</description></item>
///   <item><description>No published specification — reverse-engineered from the tool</description></item>
/// </list>
/// </summary>
public sealed class DcsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new PackDiskReader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new PackDiskWriter("DCS\0");
        foreach (var (_, d) in files) w.AddTrack(d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }

  public string Id => "Dcs";
  public string DisplayName => "DCS (Amiga Disk Archiver)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dcs";
  public IReadOnlyList<string> Extensions => [".dcs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xpk", "XPK")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga DCS disk archive (XPK compression)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PackDiskReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.CompressedSize, "XPK", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PackDiskReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new PackDiskWriter("DCS\0");
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddTrack(i.ReadContent());
    }
    w.WriteTo(output);
  }
}
