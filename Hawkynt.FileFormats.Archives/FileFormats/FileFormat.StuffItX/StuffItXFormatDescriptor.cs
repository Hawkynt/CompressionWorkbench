#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.StuffItX;

/// <summary>
/// StuffIt X (.sitx) archive (Aladdin Systems / Smith Micro): read, and written stored, Deflate-, Brimstone-, Darkhorse-, Cyanide-, Blend- or Obscured-coded,
/// optionally behind the x86 preprocessor.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/MacPaw/XADMaster</c> — XADMaster (The Unarchiver), the reader this one is converted from</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/StuffIt</c> — Wikipedia — covers StuffIt X</description></item>
/// </list>
/// </summary>
public sealed class StuffItXFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "StuffItX";
  public string DisplayName => "StuffIt X";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".sitx";
  public IReadOnlyList<string> Extensions => [".sitx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("StuffIt!"u8.ToArray(), Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("deflate", "Deflate (method 3)"), new("brimstone", "Brimstone (method 0, PPMd)"),
    new("darkhorse", "Darkhorse (method 2, LZ77)"), new("cyanide", "Cyanide (method 1, BWT)"),
    new("blend", "Blend (method 4, per-segment choice)"), new("obscured", "Obscured (method 5, RC4)"),
    new("store", "Stored"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "StuffIt X archive (Aladdin/Smith Micro): files, folders and resource forks (as .rsrc) from " +
    "Brimstone, Cyanide, Darkhorse, Deflate, Blend, Iron and stored streams, also behind the English " +
    "and x86 preprocessors, each checked against its CRC-32; written stored, Deflate-, Brimstone-, Darkhorse-, Cyanide-, Blend- or " +
    "Obscured-coded, optionally behind the x86 preprocessor. Method 7 " +
    "and encrypted streams are listed but not extracted.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new StuffItXReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.OriginalSize,
      e.CompressedSize, e.Method, e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new StuffItXReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    if (!string.IsNullOrEmpty(options.Password))
      throw new NotSupportedException("StuffIt X encryption is not written: its scheme is not known.");
    var name = (options.MethodName ?? "deflate").Trim().ToLowerInvariant();
    var x86 = name.EndsWith("+x86", StringComparison.Ordinal);
    if (x86) name = name[..^4];
    var compression = name switch {
      "" or "deflate" or "sitx" => StuffItXCompression.Deflate,
      "store" or "stored" => StuffItXCompression.Store,
      "brimstone" or "ppmd" => StuffItXCompression.Brimstone,
      "darkhorse" => StuffItXCompression.Darkhorse,
      "cyanide" => StuffItXCompression.Cyanide,
      "blend" => StuffItXCompression.Blend,
      "obscured" or "rc4" => StuffItXCompression.Obscured,
      var m => throw new NotSupportedException($"StuffIt X method '{m}' is not written (deflate, brimstone, darkhorse, cyanide, blend, obscured, store; append +x86 for the x86 preprocessor)."),
    };
    StuffItXWriter.Write(output, inputs.Select(i => new StuffItXWriter.Input(i.ArchiveName, i.IsDirectory ? null : i.ReadContent(),
      i.Metadata?.LastWriteTimeUtc?.UtcDateTime)), compression, x86);
  }
}
