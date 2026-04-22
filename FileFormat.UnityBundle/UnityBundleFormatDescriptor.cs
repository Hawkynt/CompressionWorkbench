#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.UnityBundle;

/// <summary>
/// Unity Asset Bundle (<c>.unity3d</c> / <c>.assets</c> / <c>.bundle</c>) — the UnityFS container
/// that ships serialized Unity assets bundled for runtime loading. Each bundled asset is listed
/// as a Node entry (path from the internal directory). Storage blocks can be stored, LZMA, or
/// LZ4/LZ4HC-compressed; all four are supported.
/// </summary>
public sealed class UnityBundleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "UnityBundle";
  public string DisplayName => "Unity Asset Bundle";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".bundle";
  public IReadOnlyList<string> Extensions => [".bundle", ".unity3d", ".assetbundle"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("UnityFS\0"u8.ToArray(), Confidence: 0.95),
    new("UnityWeb\0"u8.ToArray(), Confidence: 0.90),
    new("UnityRaw\0"u8.ToArray(), Confidence: 0.90),
    new("UnityArchive\0"u8.ToArray(), Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("lzma", "LZMA"),
    new("lz4", "LZ4"),
    new("lz4hc", "LZ4HC"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unity Engine asset bundle (UnityFS: stored/LZMA/LZ4/LZ4HC blocks).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = Open(stream);
    var entries = new List<ArchiveEntryInfo>(reader.Nodes.Count);
    for (var i = 0; i < reader.Nodes.Count; ++i) {
      var n = reader.Nodes[i];
      entries.Add(new ArchiveEntryInfo(
        Index: i,
        Name: n.Path,
        OriginalSize: n.Size,
        CompressedSize: n.Size,
        Method: MethodLabel(reader),
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: null));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = Open(stream);
    if (reader.Nodes.Count == 0)
      return; // legacy UnityWeb/UnityRaw — no node directory to surface
    if (!reader.CanExtract) {
      // Unsupported block compression — fall back to listing only.
      return;
    }

    foreach (var node in reader.Nodes) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(node.Path, files))
        continue;
      var data = reader.ExtractNode(node);
      FormatHelpers.WriteFile(outputDir, node.Path, data);
    }
  }

  private static string MethodLabel(UnityBundleReader reader) {
    if (reader.Blocks.Count == 0) return reader.Signature;
    // Report the compression of the first block; bundles commonly use a uniform method.
    var c = reader.Blocks[0].Flags & 0x3F;
    return c switch {
      0 => "Stored",
      1 => "LZMA",
      2 => "LZ4",
      3 => "LZ4HC",
      _ => $"Unknown({c})"
    };
  }

  private static UnityBundleReader Open(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return new UnityBundleReader(ms.ToArray());
  }
}
