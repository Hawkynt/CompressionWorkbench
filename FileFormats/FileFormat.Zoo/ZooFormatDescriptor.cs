#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zoo;

public sealed class ZooFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Zoo";
  public string DisplayName => "ZOO";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
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
