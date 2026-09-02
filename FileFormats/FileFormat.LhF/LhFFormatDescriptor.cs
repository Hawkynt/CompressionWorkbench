#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.LhF;

/// <summary>
/// Amiga LhFloppy (LhF) disk archive storing whole floppy tracks with LZ77+Huffman compression.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://aminet.net</c> — Aminet — distribution home of the Amiga disk-archiver tooling</description></item>
///   <item><description>No published specification — format reverse-engineered from the archiver</description></item>
/// </list>
/// </summary>
public sealed class LhFFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new LhFReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "LhF";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LhF (LhFloppy)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) tracks inside an existing LhF archive. Uses
  /// <see cref="LhFModifier"/> — Add appends after the EOF position and bumps
  /// the trackCount field; Remove walks the track list and shifts trailing
  /// bytes (no central directory).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      LhFModifier.RemoveFile(archive, name, wipeData: true);
      LhFModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named tracks; uses <see cref="LhFModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      LhFModifier.RemoveFile(archive, name, wipeData: true);
  }
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".lhf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".lhf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x4C, 0x68, 0x46, 0x00], Confidence: 0.90)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("lzh", "LZH")];
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
public string Description => "Amiga LhFloppy disk archive (LZ77+Huffman per track)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LhFReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.CompressedSize, "LZH", false, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LhFReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new LhFWriter();
    var trackNum = 0;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      // Try to recover the track index from the conventional "track_NNN.raw" name
      // produced by the reader; fall back to insertion order.
      var name = Path.GetFileNameWithoutExtension(i.ArchiveName);
      var underscore = name.LastIndexOf('_');
      var explicitTrack = underscore >= 0 && int.TryParse(name[(underscore + 1)..], out var n) ? n : trackNum;
      w.AddTrack(explicitTrack, i.ReadContent());
      trackNum++;
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
        var r = new LhFReader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new LhFWriter();
        var trackNum = 0;
        foreach (var (n, d) in files) {
          // Recover the track index from the "track_NNN.raw" reader-synthesized name.
          var stem = Path.GetFileNameWithoutExtension(n);
          var underscore = stem.LastIndexOf('_');
          var explicitTrack = underscore >= 0 && int.TryParse(stem[(underscore + 1)..], out var t) ? t : trackNum;
          w.AddTrack(explicitTrack, d);
          trackNum++;
        }
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }
}
