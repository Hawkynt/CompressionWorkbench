#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;
using Compression.Registry;

namespace FileFormat.ExePackers;

/// <summary>
/// A generic adapter that wraps any <see cref="IFormatDescriptor"/> implementing
/// <see cref="IArchiveFormatOperations"/> as an <see cref="IExecutablePackerHandler"/>.
/// This allows us to reuse existing high-quality format descriptors for PE detection/extraction
/// without duplicating their parsing and signature logic.
/// </summary>
public sealed class DescriptorExecutablePackerHandler : IExecutablePackerHandler {
  private readonly IFormatDescriptor descriptor;
  private readonly IArchiveFormatOperations archiveOps;

  /// <summary>
  /// Initializes a new instance of <see cref="DescriptorExecutablePackerHandler"/>.
  /// </summary>
  public DescriptorExecutablePackerHandler(IFormatDescriptor descriptor) {
    this.descriptor = descriptor;
    this.archiveOps = (IArchiveFormatOperations)descriptor;
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => this.descriptor.Id.ToLowerInvariant();
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => this.descriptor.DisplayName;

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    try {
      using var ms = new MemoryStream(image.ToArray());
      var entries = this.archiveOps.List(ms, null);
      if (entries.Count > 0) {
        // High confidence match since the format descriptor successfully parsed the structures
        return new(true, this.Id, 0.9, []);
      }
    } catch {
      // Ignored
    }

    return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, $"{this.DisplayName} signature not matched.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      info,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = this.descriptor.Id,
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();

    try {
      using var ms = new MemoryStream(packed.OriginalImage);
      var entries = this.archiveOps.List(ms, null);
      foreach (var entry in entries) {
        var data = this.archiveOps.ExtractEntryToMemory(ms, entry.Name, null);
        var method = entry.Method ?? "stored";
        artifacts.Add(new(entry.Name, data, method));
      }
    } catch (Exception ex) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, $"Failed to extract entries: {ex.Message}", true));
    }

    var level = artifacts.Any(a => a.Name == "packed_payload.bin" || a.Name == "compressed_payload.bin")
      ? ExecutableUnpackLevel.PayloadLocated
      : ExecutableUnpackLevel.DetectionOnly;

    diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
      $"{this.DisplayName} detected. Managed decompression/transform reversal remains planned; protection is anti-RE or virtualizer-based.",
      true));

    var result = new UnpackResult(level, this.Capabilities, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"packer\": \"{this.Id}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
