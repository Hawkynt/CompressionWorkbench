#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PackDisk;

/// <summary>
/// Amiga PackDisk disk archive — whole-floppy track data compressed with the XPK library.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://aminet.net</c> — Aminet — distribution home of the archiver and the XPK compression library</description></item>
///   <item><description>XPK master library developer documentation (Amiga) — defines the XPKF container the track data is stored in</description></item>
///   <item><description>No published specification — reverse-engineered from the tool</description></item>
/// </list>
/// </summary>
public sealed class PackDiskFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "PackDisk";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PackDisk (Amiga)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".pdsk";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".pdsk"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("PDSK"u8.ToArray(), Confidence: 0.85)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("xpk", "XPK"), new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Amiga PackDisk disk archive (XPK compression)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PackDiskReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.CompressedSize,
      e.CompressedSize == e.Size ? "Stored" : "XPK", false, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PackDiskReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: emit stored tracks (no XPK encoder needed). The reader recognises
    // a track as stored when no "XPKF" chunk header precedes the 5632-byte block.
    var w = new PackDiskWriter("PDSK");
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddTrack(i.ReadContent());
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new PackDiskReader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new PackDiskWriter("PDSK");
        foreach (var (_, d) in files) w.AddTrack(d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }
}
