#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.LhF;

public sealed class LhFFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "LhF";
  public string DisplayName => "LhF (LhFloppy)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lhf";
  public IReadOnlyList<string> Extensions => [".lhf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x4C, 0x68, 0x46, 0x00], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzh", "LZH")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga LhFloppy disk archive (LZ77+Huffman per track)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LhFReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.CompressedSize, "LZH", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LhFReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

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
      w.AddTrack(explicitTrack, File.ReadAllBytes(i.FullPath));
      trackNum++;
    }
    w.WriteTo(output);
  }
}
