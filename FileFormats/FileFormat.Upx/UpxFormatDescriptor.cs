#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Upx;

/// <summary>
/// Pseudo-archive descriptor for UPX-packed executables. The archive facade
/// stays compatible with CW's List/Extract model, while the actual unpacking
/// work is delegated to <see cref="UpxExecutablePackerHandler"/>.
/// </summary>
public sealed class UpxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Upx";
  public string DisplayName => "UPX-packed executable";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".exe";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "UPX-packed executable (PE / ELF / Mach-O) - surfaces legacy UPX pseudo-archive entries " +
    "plus executable-unpacking diagnostics, decompressed payloads, memory images, and rebuilt PE output when available.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        e.Method, false, false, null))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static IEnumerable<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    foreach (var artifact in UpxExecutablePackerHandler.Unpack(ms.GetBuffer().AsSpan(0, (int)ms.Length)).Artifacts)
      yield return (artifact.Name, artifact.Data, artifact.Method);
  }
}
