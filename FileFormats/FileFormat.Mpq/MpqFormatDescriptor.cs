#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mpq;

public sealed class MpqFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the MPQ archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the MPQ archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new MpqReader(stream);
        var list = new List<(string Name, byte[] Data)>();
        foreach (var e in r.Entries) {
          if (!e.Exists) continue;
          try { list.Add((e.FileName, r.Extract(e))); } catch { /* skip unreadable */ }
        }
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new MpqWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    MpqReader r;
    try {
      archive.Position = 0;
      r = new MpqReader(archive);
    } catch {
      yield break;
    }
    // MPQ header is 32 bytes (v1) at _headerOffset
    yield return new DefragBlockInfo(r.HeaderOffset, 32, DefragBlockKind.MetadataReserved, FileName: "MPQ Header");
    foreach (var e in r.Entries) {
      if (!e.Exists || e.CompressedSize <= 0) continue;
      yield return new DefragBlockInfo(r.HeaderOffset + e.FileOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
    }
  }

  public string Id => "Mpq";
  public string DisplayName => "MPQ";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mpq";
  public IReadOnlyList<string> Extensions => [".mpq"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'M', (byte)'P', (byte)'Q', 0x1A], Confidence: 0.95),
    new([(byte)'M', (byte)'P', (byte)'Q', 0x1B], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mpq", "MPQ")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Blizzard MPQ game archive (Diablo/StarCraft/WoW)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MpqReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.IsCompressed ? "Compressed" : "Stored", false, e.IsEncrypted, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MpqReader(stream);
    foreach (var e in r.Entries) {
      if (!e.Exists) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      try { WriteFile(outputDir, e.FileName, r.Extract(e)); } catch { }
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: produce a v1 MPQ with stored (uncompressed) file entries plus an
    // auto-generated "(listfile)" so file names roundtrip. Compression isn't
    // emitted -- the existing per-method decoders (zlib/bzip2/PKWARE/Huffman)
    // don't have paired encoders here, and stored files are valid MPQ entries.
    var w = new MpqWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
