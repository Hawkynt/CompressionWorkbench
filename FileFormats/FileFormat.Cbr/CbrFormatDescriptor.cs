#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cbr;

public sealed class CbrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => FileFormat.Rar.RarLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag delegating to RAR (CBR is a RAR variant).</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag delegating to RAR (CBR is a RAR variant).</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new FileFormat.Rar.RarReader(stream);
        var list = new List<(string Name, byte[] Data)>();
        for (var i = 0; i < r.Entries.Count; i++) {
          var e = r.Entries[i];
          if (e.IsDirectory) continue;
          list.Add((e.Name, r.Extract(i)));
        }
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new FileFormat.Rar.RarWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddFile(n, d);
          w.Finish();
        }
        return ms.ToArray();
      });
  }

  public string Id => "Cbr";
  public string DisplayName => "CBR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsPassword;
  public string DefaultExtension => ".cbr";
  public IReadOnlyList<string> Extensions => [".cbr"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rar", "RAR")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Comic book RAR archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FileFormat.Rar.RarReader(stream, password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      $"Method{e.CompressionMethod}", e.IsDirectory, false, e.ModifiedTime?.DateTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FileFormat.Rar.RarReader(stream, password);
    for (var i = 0; i < r.Entries.Count; i++) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new FileFormat.Rar.RarWriter(output, leaveOpen: true, password: options.Password);
    foreach (var i in inputs) {
      // RAR stores directory structure implicitly via entry path components;
      // skip explicit directory inputs (mirrors how the RarReader exposes them).
      if (i.IsDirectory) continue;
      var modified = File.Exists(i.FullPath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(i.FullPath), TimeSpan.Zero) : (DateTimeOffset?)null;
      w.AddFile(i.ArchiveName, i.ReadContent(), modified);
    }
  }
}
