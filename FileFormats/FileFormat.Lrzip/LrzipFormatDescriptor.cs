#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lrzip;

public sealed class LrzipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Lrzip";
  public string DisplayName => "Long Range Zip";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lrz";
  public IReadOnlyList<string> Extensions => [".lrz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("LRZI"u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lrzip-lzma", "LRZIP LZMA")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Long Range Zip (LZMA subtype only)";

  // The synthetic single entry name we expose; lrzip is a single-stream compressor,
  // not a true archive, so we surface the payload as one entry called "data".
  private const string EntryName = "data";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LrzipReader(stream, leaveOpen: true);
    var method = r.Method switch {
      LrzipConstants.MethodNone  => "Stored",
      LrzipConstants.MethodLzma  => "LZMA",
      LrzipConstants.MethodLzo   => "LZO",
      LrzipConstants.MethodBzip2 => "BZIP2",
      LrzipConstants.MethodGzip  => "GZIP",
      LrzipConstants.MethodZpaq  => "ZPAQ",
      _ => $"Method{r.Method}"
    };
    // CompressedSize is the body length on disk; we use the stream length minus header
    // since lrzip does not record it explicitly.
    var compressed = Math.Max(0L, stream.Length - LrzipConstants.HeaderSize);
    return [new ArchiveEntryInfo(0, EntryName, (long)r.ExpandedSize, compressed, method, false, false, null)];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LrzipReader(stream, leaveOpen: true);
    if (files != null && !MatchesFilter(EntryName, files))
      return;
    WriteFile(outputDir, EntryName, r.Extract());
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // lrzip is a single-stream compressor, so we collapse all non-directory inputs into the
    // first one we see. Concatenation across multiple inputs is intentionally not done —
    // callers wanting that should tar first.
    var files = FlatFiles(inputs).ToArray();
    if (files.Length == 0)
      throw new InvalidOperationException("Lrzip requires exactly one input file.");
    var (_, data) = files[0];
    var w = new LrzipWriter();
    w.Write(data, output);
  }
}
