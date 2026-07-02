#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zap;

/// <summary>
/// Amiga ZAP disk archive — LZ77+RLE backward-bitstream disk packer.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://aminet.net/</c> — Aminet — distribution home of the Amiga ZAP disk archiver</description></item>
///   <item><description>no formal spec; format known from the tool's own documentation and depacker sources</description></item>
/// </list>
/// </summary>
public sealed class ZapFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "ZAP archives carry Amiga disk tracks indexed by track number — track positions are part of " +
      "the floppy geometry, so defragmentation isn't meaningful.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new ZapReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "Zap";
  public string DisplayName => "ZAP (Amiga Disk Archiver)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".zap";
  public IReadOnlyList<string> Extensions => [".zap"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x5A, 0x41, 0x50, 0x00], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzrle", "LZ77+RLE")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga ZAP disk archive (LZ77+RLE backward bitstream)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZapReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.CompressedSize, e.IsCompressed ? "LZ77+RLE" : "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZapReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new ZapWriter();
    var trackNum = 0;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      // Recover track index from "track_NNN.raw" naming when present; else
      // fall back to insertion order.
      var name = Path.GetFileNameWithoutExtension(i.ArchiveName);
      var underscore = name.LastIndexOf('_');
      var explicitTrack = underscore >= 0 && int.TryParse(name[(underscore + 1)..], out var n) ? n : trackNum;
      w.AddTrack(explicitTrack, i.ReadContent());
      trackNum++;
    }
    w.WriteTo(output);
  }
}
