#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lzh;

public sealed class LzhFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Lzh";
  public string DisplayName => "LZH";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lzh";
  public IReadOnlyList<string> Extensions => [".lzh", ".lha"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'-', (byte)'l'], Offset: 2, Confidence: 0.70)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("lh5", "LH5"), new("lh0", "LH0 (Store)"), new("lh1", "LH1"), new("lh4", "LH4"),
    new("lh6", "LH6"), new("lh7", "LH7"), new("lzs", "LZS"), new("lz5", "LZ5"),
    new("pm0", "PM0"), new("pm1", "PM1"), new("pm2", "PM2")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "LHA/LZH archive, popular in Japan, Amiga";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LhaReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.Method, false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LhaReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var lzhMethod = options.MethodName switch {
      "lh0" or "store" => LhaConstants.MethodLh0,
      "lh1" => LhaConstants.MethodLh1,
      "lh2" => LhaConstants.MethodLh2,
      "lh3" => LhaConstants.MethodLh3,
      "lh4" => LhaConstants.MethodLh4,
      "lh6" => LhaConstants.MethodLh6,
      "lh7" => LhaConstants.MethodLh7,
      "lzs" => LhaConstants.MethodLzs,
      "lz5" => LhaConstants.MethodLz5,
      "pm0" => LhaConstants.MethodPm0,
      "pm1" => LhaConstants.MethodPm1,
      "pm2" => LhaConstants.MethodPm2,
      _ => LhaConstants.MethodLh5,
    };
    var w = new LhaWriter(lzhMethod);
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }
}
