#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.SplitFile;

/// <summary>
/// Split-file volume set (.001/.002 ...) — raw sequential byte slices joined back into one file.
///
/// References:
/// <list type="bullet">
///   <item><description>de-facto convention (no formal spec): headerless sequential byte splits, popularized by HJSplit and Total Commander</description></item>
///   <item><description>7-Zip and WinRAR use the same numeric-suffix naming for raw split volumes</description></item>
/// </list>
/// </summary>
public sealed class SplitFileFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "Split-file (.001/.002/...) is a multi-part file join — defragmentation isn't meaningful for a stream view.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  public string Id => "SplitFile";
  public string DisplayName => "Split File (.001)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest;
  public string DefaultExtension => ".001";
  public IReadOnlyList<string> Extensions => [".001"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Split file parts (.001, .002, ...) joined into a single file";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    // Split files need filesystem access (multiple files), not a single stream.
    // When invoked from a stream, we report the stream as a single entry.
    return [new ArchiveEntryInfo(0, "joined", stream.Length, stream.Length,
      "Stored", false, false, null)];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // For stream-based extraction, just copy the stream content.
    // Real split file joining requires filesystem paths (handled by CLI/UI layer).
    var outputPath = Path.Combine(outputDir, "joined");
    Directory.CreateDirectory(outputDir);
    using var fs = File.Create(outputPath);
    stream.CopyTo(fs);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // SplitFile Create joins all input files sequentially into one output stream.
    foreach (var (_, data) in FormatHelpers.FilesOnly(inputs))
      output.Write(data);
  }
}
