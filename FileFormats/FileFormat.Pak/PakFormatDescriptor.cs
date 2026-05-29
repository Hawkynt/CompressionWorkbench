#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pak;

public sealed class PakFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the PAK archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the PAK archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new PakReader(stream);
        var list = new List<(string, byte[])>();
        while (r.GetNextEntry() is { } e)
          list.Add((e.FileName, r.ReadEntryData()));
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new PakWriter(ms);
        foreach (var (n, d) in files) w.AddEntry(n, d);
        w.Finish();
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new Arc.ArcReader(archive);
    while (r.GetNextEntry() is { } e) {
      var headerSize = e.Method >= Arc.ArcConstants.MethodStored ? Arc.ArcConstants.NewHeaderSize : Arc.ArcConstants.OldHeaderSize;
      var dataStart = archive.Position;
      var headerStart = dataStart - headerSize;
      yield return new DefragBlockInfo(headerStart, headerSize, DefragBlockKind.MetadataReserved, FileName: "Header: " + e.FileName);
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(dataStart, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
      archive.Position = dataStart + e.CompressedSize;
    }
    var eoaPos = archive.Position - 2;
    if (eoaPos >= 0)
      yield return new DefragBlockInfo(eoaPos, 2, DefragBlockKind.MetadataReserved, FileName: "End-of-archive");
  }

  public string Id => "Pak";
  public string DisplayName => "PAK";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pak";
  public IReadOnlyList<string> Extensions => [".pak"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("pak", "PAK")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Quake PAK game resource archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PakReader(stream);
    var entries = new List<ArchiveEntryInfo>();
    var i = 0;
    while (r.GetNextEntry() is { } e)
      entries.Add(new(i++, e.FileName, e.OriginalSize, e.CompressedSize,
        $"Method {e.Method}", false, false, e.LastModified.DateTime));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PakReader(stream);
    while (r.GetNextEntry() is { } e) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ReadEntryData());
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new PakWriter(output);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddEntry(name, data);
    w.Finish();
  }
}
