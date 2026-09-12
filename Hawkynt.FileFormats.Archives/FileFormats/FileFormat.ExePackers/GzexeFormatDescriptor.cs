#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.Streams;
using Compression.Registry;
using FileFormat.Gzip;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for GNU gzip's gzexe wrapper. gzexe is listed by
/// Packing Box as an ELF packer, but the produced file is a POSIX shell script
/// with an embedded gzip member. Static unpacking is therefore deterministic:
/// find the gzip member, inflate it, and emit the original executable bytes.
/// </summary>
public sealed class GzexeFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Gzexe";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "gzexe executable wrapper";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  // A gzexe file is a POSIX shell script (content/marker-detected, no canonical
  // extension); ".sh" is the honest suggested-output extension. Extensions stays
  // empty so it never registers a detection extension that would collide.
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".sh";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("gzip", "Gzip"), new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "GNU gzexe shell-script executable wrapper - statically extracts the embedded gzip member and reconstructed original executable.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.CompressedSize,
        e.Method, false, false, null))
      .ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  internal static List<(string Name, byte[] Data, long CompressedSize, string Method)> BuildArtifacts(byte[] bytes) {
    var payloadOffset = LocateEmbeddedGzip(bytes);
    if (payloadOffset < 0)
      throw new InvalidDataException("gzexe: no embedded gzip payload was found in a gzexe-like shell wrapper.");

    var compressed = bytes[payloadOffset..];
    var reconstructed = InflateGzip(compressed);
    return [
      ("metadata.ini", BuildMetadata(bytes, payloadOffset, compressed.Length, reconstructed.Length), compressed.Length, "stored"),
      ("diagnostics.json", BuildDiagnosticsJson(payloadOffset, compressed.Length, reconstructed.Length), compressed.Length, "stored"),
      ("original_packed.bin", bytes, bytes.Length, "stored"),
      ("compressed_payload.gz", compressed, compressed.Length, "gzip"),
      ("reconstructed/original_executable.bin", reconstructed, compressed.Length, "stored"),
    ];
  }

  private static List<(string Name, byte[] Data, long CompressedSize, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var bytes = ms.ToArray();
    return BuildArtifacts(bytes);
  }

  internal static int LocateEmbeddedGzip(byte[] bytes) {
    var scriptPrefixLength = Math.Min(bytes.Length, 8192);
    var prefix = Encoding.ASCII.GetString(bytes, 0, scriptPrefixLength);
    if (!prefix.StartsWith("#!", StringComparison.Ordinal) ||
        !prefix.Contains("gzip", StringComparison.OrdinalIgnoreCase))
      return -1;

    for (var i = 0; i + 10 <= bytes.Length; i++) {
      if (bytes[i] != 0x1F || bytes[i + 1] != 0x8B || bytes[i + 2] != 8) continue;
      try {
        _ = InflateGzip(bytes[i..]);
        return i;
      } catch (InvalidDataException) {
      } catch (EndOfStreamException) {
      }
    }

    return -1;
  }

  private static byte[] InflateGzip(byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var gzip = new GzipStream(input, CompressionStreamMode.Decompress, leaveOpen: false);
    using var output = new MemoryStream();
    gzip.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] BuildMetadata(byte[] image, int payloadOffset, int compressedSize, int reconstructedSize) {
    var sb = new StringBuilder();
    sb.AppendLine("[gzexe]");
    sb.Append(CultureInfo.InvariantCulture, $"image_size = {image.Length}\n");
    sb.Append(CultureInfo.InvariantCulture, $"payload_offset = 0x{payloadOffset:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"compressed_size = {compressedSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"reconstructed_size = {reconstructedSize}\n");
    sb.AppendLine("capability_level = RebuiltExecutable");
    sb.AppendLine("note = gzexe wraps the original executable in a shell script with an embedded gzip member; no input code is executed.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildDiagnosticsJson(int payloadOffset, int compressedSize, int reconstructedSize) =>
    Encoding.UTF8.GetBytes(
      $$"""
      {
        "packer": "gzexe",
        "container": "shell-script-wrapper",
        "capabilityLevel": "RebuiltExecutable",
        "canRebuildExecutable": true,
        "payloadOffset": {{payloadOffset}},
        "compressedSize": {{compressedSize}},
        "reconstructedSize": {{reconstructedSize}},
        "warnings": [
          "gzexe output is a POSIX shell script wrapper, not the same executable container as the original."
        ],
        "outputs": [
          "compressed_payload.gz",
          "reconstructed/original_executable.bin",
          "metadata.ini",
          "diagnostics.json"
        ]
      }
      """);
}
