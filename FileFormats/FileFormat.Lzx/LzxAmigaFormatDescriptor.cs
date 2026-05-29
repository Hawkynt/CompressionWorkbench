#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lzx;

public sealed class LzxAmigaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the LZX archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the LZX archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new LzxAmigaReader(stream);
        return r.Entries.Select(e => (e.FileName, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new LzxAmigaWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddFile(n, d, DateTime.Now);
        }
        return ms.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    // 3-byte magic "LZX"
    yield return new DefragBlockInfo(0, LzxAmigaConstants.MagicLength, DefragBlockKind.MetadataReserved, FileName: "LZX Magic");
    LzxAmigaReader r;
    try {
      r = new LzxAmigaReader(archive, leaveOpen: true);
    } catch {
      yield break;
    }
    foreach (var e in r.Entries) {
      var headerSize = e.DataOffset - (e.DataOffset - LzxAmigaConstants.FixedHeaderSize - e.FileName.Length - e.Comment.Length);
      // Header starts just before DataOffset by (FixedHeaderSize + filenameLen + commentLen) bytes
      var hdrLen = LzxAmigaConstants.FixedHeaderSize + e.FileName.Length + e.Comment.Length;
      var hdrStart = e.DataOffset - hdrLen;
      if (hdrStart >= 0 && hdrLen > 0)
        yield return new DefragBlockInfo(hdrStart, hdrLen, DefragBlockKind.MetadataReserved, FileName: "Header: " + e.FileName);
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
    }
  }

  public string Id => "LzxAmiga";
  public string DisplayName => "LZX (Amiga)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lzx";
  public IReadOnlyList<string> Extensions => [".lzx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'L', (byte)'Z', (byte)'X'], Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzx", "LZX")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga LZX archive with LZ+Huffman";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LzxAmigaReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.Method == 0 ? "Stored" : "LZX", false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LzxAmigaReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new LzxAmigaWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data, DateTime.Now);
  }
}
