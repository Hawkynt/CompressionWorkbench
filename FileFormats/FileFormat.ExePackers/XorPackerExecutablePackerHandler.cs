#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Represents a xor packer executable packer handler.
/// </summary>
public sealed class XorPackerExecutablePackerHandler : IExecutablePackerHandler {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "xor_packer";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Xor_Packer .NET PE wrapper";

  private static ReadOnlySpan<byte> Marker => "***"u8;

    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe;

    /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Xor_Packer: not a valid PE wrapper.", true)]);

    var match = TryRecover(image, out var payload, out _);
    return match
      ? new(true, this.Id, 0.98, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Xor_Packer appended settings marker or recoverable PE payload was not found.", true)]);
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
        ["packer"] = "Xor_Packer",
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

    if (!TryRecover(packed.OriginalImage, out var payload, out var decoded)) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
        "Xor_Packer marker was found only if the appended Base64/XOR payload could be decoded to a PE image.", true));
      var failed = new UnpackResult(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, artifacts, diagnostics);
      artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, failed), "stored"));
      return failed with { Artifacts = artifacts };
    }

    artifacts.Add(new("compressed_payload.txt", payload, "xor-packer-settings"));
    artifacts.Add(new("decompressed_payload.bin", decoded, "xor-base64"));
    artifacts.Add(new("reconstructed/reconstructed.exe", decoded, "stored"));
    diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
      "Xor_Packer embeds the original PE as an encoded payload. The reconstructed executable is the embedded PE bytes; no wrapper code was executed."));

    var caps = this.Capabilities;
    var innerInfo = ExecutableContainerParsers.ParseBestEffort(decoded);
    caps |= innerInfo.Architecture switch {
      CpuArchitecture.X86 => ExecutableUnpackCapabilities.SupportsX86,
      CpuArchitecture.X64 => ExecutableUnpackCapabilities.SupportsX64,
      _ => ExecutableUnpackCapabilities.None,
    };

    var result = new UnpackResult(ExecutableUnpackLevel.RebuiltExecutable, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, innerInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  internal static bool TryRecover(ReadOnlySpan<byte> image, out byte[] payloadSettings, out byte[] decodedPe) {
    payloadSettings = [];
    decodedPe = [];
    var markerOffset = image.LastIndexOf(Marker);
    if (markerOffset < 0)
      return false;

    var settings = Encoding.ASCII.GetString(image[(markerOffset + Marker.Length)..]).TrimEnd('\0', '\r', '\n', ' ');
    var separator = settings.IndexOf('|', StringComparison.Ordinal);
    if (separator <= 0 || separator == settings.Length - 1)
      return false;

    var encodedPayload = settings[..separator];
    var encodedKey = settings[(separator + 1)..];
    try {
      var key = XorString(Encoding.UTF8.GetString(Convert.FromBase64String(encodedKey)), "randomkey");
      if (key.Length == 0)
        return false;
      var xoredBase64 = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPayload));
      var originalBase64 = XorString(xoredBase64, key);
      var candidate = Convert.FromBase64String(originalBase64);
      if (!PackerScanner.IsPe(candidate))
        return false;
      payloadSettings = Encoding.ASCII.GetBytes(settings);
      decodedPe = candidate;
      return true;
    } catch (FormatException) {
      return false;
    } catch (ArgumentException) {
      return false;
    }
  }

  private static string XorString(string data, string key) {
    var output = new char[data.Length];
    for (var i = 0; i < data.Length; i++)
      output[i] = (char)(data[i] ^ key[i % key.Length]);
    return new string(output);
  }

  private static byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"xor_packer\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"transform\": \"base64-xor-base64\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
