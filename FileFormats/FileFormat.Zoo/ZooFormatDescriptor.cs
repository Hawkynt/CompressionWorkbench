#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zoo;

public sealed class ZooFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Zoo";
  public string DisplayName => "ZOO";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Zoo archive.
  /// Uses <see cref="ZooModifier"/> — Add walks the linked-list chain to
  /// the tail, writes a Stored entry at end-of-stream, and patches the
  /// previous tail's <c>nextOffset</c> link.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      ZooModifier.RemoveFile(archive, name, wipeData: true);
      ZooModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="ZooModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ZooModifier.RemoveFile(archive, name, wipeData: true);
  }

  public string DefaultExtension => ".zoo";
  public IReadOnlyList<string> Extensions => [".zoo"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'Z', (byte)'O', (byte)'O'], Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzw", "LZW"), new("store", "Store")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Zoo archive, early DOS compressor by Rahul Dhesi";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZooReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.EffectiveName, e.OriginalSize, e.CompressedSize,
      e.CompressionMethod.ToString(), false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZooReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.EffectiveName, files)) continue;
      WriteFile(outputDir, e.EffectiveName, r.ExtractEntry(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var zooMethod = options.MethodName switch {
      "store" => ZooCompressionMethod.Store,
      _ => ZooCompressionMethod.Lzw,
    };
    var w = new ZooWriter(output, defaultMethod: zooMethod);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddEntry(name, data);
    w.Finish();
  }
}
