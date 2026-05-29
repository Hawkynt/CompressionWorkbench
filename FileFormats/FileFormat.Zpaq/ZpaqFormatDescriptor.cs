#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zpaq;

public sealed class ZpaqFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {

  /// <summary>Rebuild-based defrag: extracts then re-creates the ZPAQ archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the ZPAQ archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ZpaqReader(stream);
        var list = new List<(string, byte[])>();
        foreach (var e in r.Entries) {
          if (e.IsDirectory) continue;
          using var es = r.Extract(e);
          using var ms = new MemoryStream();
          es.CopyTo(ms);
          list.Add((e.FileName, ms.ToArray()));
        }
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new ZpaqWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddFile(n, d);
        }
        return ms.ToArray();
      });
  }

  public string Id => "Zpaq";
  public string DisplayName => "ZPAQ";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".zpaq";
  public IReadOnlyList<string> Extensions => [".zpaq"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'z', (byte)'P', (byte)'Q'], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zpaq", "ZPAQ")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZPAQ journaling archive with deduplication";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZpaqReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.Size, e.CompressedSize,
      "zpaq", e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZpaqReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      using var es = r.Extract(e);
      using var ms = new MemoryStream();
      es.CopyTo(ms);
      WriteFile(outputDir, e.FileName, ms.ToArray());
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new ZpaqWriter(output, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.ArchiveName);
      } else {
        var data = File.ReadAllBytes(input.FullPath);
        w.AddFile(input.ArchiveName, data);
      }
    }
  }
}
