#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Sqx;

/// <summary>
/// SQX archives (Squeez, formats 1.1 and 2.0). Reads stored and LZ77 + Huffman files (methods
/// 1-4, solid chains, multimedia blocks, delta and x86 filters); writes them; refuses audio,
/// text and ultra methods, encryption and split files. See <see cref="SqxReader"/>.
/// </summary>
public sealed class SqxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the SQX archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the SQX archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new SqxReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.ExtractEntry(e))).ToList();
      },
      buildImage: files => {
        var w = new SqxWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new SqxReader(archive, leaveOpen: true);
    foreach (var e in r.Entries)
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
  }

  public string Id => "Sqx";
  public string DisplayName => "SQX";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: Add/Replace/Remove go through the verified extract -> edit -> re-create rebuild.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sqx";
  public IReadOnlyList<string> Extensions => [".sqx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("-sqx-"u8.ToArray(), Offset: 7, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("sqx", "SQX")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "SQX (Squeez) archive, formats 1.1 and 2.0, LZ77 + Huffman methods";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SqxReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      $"SQX {e.Method}", e.IsDirectory, e.IsEncrypted, e.LastModified == DateTime.MinValue ? null : e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SqxReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var dictionary = 256 * 1024;
    if (options.DictSize > 0) {
      dictionary = 32 * 1024;
      while (dictionary < options.DictSize && dictionary < 4 << 20)
        dictionary <<= 1;
    }

    var method = (byte)Math.Clamp(options.Level is { } level ? (level + 2) / 3 + 1 : 2, 1, 4);
    var w = new SqxWriter(method, dictionary);
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }
}
