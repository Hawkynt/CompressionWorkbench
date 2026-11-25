#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lhf;

/// <summary>
/// Amiga LhF 1.03 archive (Stefan Pampin, 1993; magic <c>LhF\0</c>): files stored or packed with LhF's
/// own LZ/Huffman coder. Read-only. LhF names its archives ".f", which Freeze also uses, so detection
/// is by magic.
///
/// References:
/// <list type="bullet">
///   <item><description><c>docs/LHF-ON-DISK.md</c> — layout and coder, checked against LhF 1.03 output</description></item>
///   <item><description><c>https://github.com/MacPaw/XADMaster</c> — libxad's LhF client (<c>libxad/clients/LhF.c</c>)</description></item>
/// </list>
/// </summary>
public sealed class LhfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Lhf";
  public string DisplayName => "LhF (Amiga)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.SupportsMultipleEntries
    | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".f";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("LhF\0"u8.ToArray(), Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lhf", "LhF LZ/Huffman"), new("store", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga LhF file archive (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new LhfReader(stream);
      return [.. r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.StoredSize,
        e.IsDirectory ? "dir" : e.IsStored ? "store" : "lhf", e.IsDirectory, false, e.LastModified))];
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.f", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LhfReader(stream);
    foreach (var e in r.Entries) {
      if (files is { Length: > 0 } && !MatchesFilter(e.Name, files))
        continue;
      if (e.IsDirectory)
        CreateEntryDirectory(outputDir, e.Name);
      else
        WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
