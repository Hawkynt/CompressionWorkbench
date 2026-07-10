#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

public sealed class MPressExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "mpress";
  public string DisplayName => "MPRESS executable packer";

  private static ReadOnlySpan<byte> MPressLiteral => "MPRESS"u8;
  private static ReadOnlySpan<byte> MatcodeLiteral => "MATCODE"u8;

  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsX86 |
    ExecutableUnpackCapabilities.SupportsX64;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var isPe = PackerScanner.IsPe(image);
    var isElf = image.Length >= 4 && image[0] == 0x7F && image[1] == (byte)'E' && image[2] == (byte)'L' && image[3] == (byte)'F';
    if (!isPe && !isElf)
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "MPRESS: not a PE or ELF executable.", true)]);

    var hasPeSection = isPe && PackerScanner.GetPeSections(image)
      .Any(s => s.Name.StartsWith(".MPRESS", StringComparison.OrdinalIgnoreCase));
    var hasLiteral = PackerScanner.IndexOfBounded(image, MPressLiteral, 0x10000) >= 0 ||
      PackerScanner.IndexOfBounded(image, MatcodeLiteral, 0x10000) >= 0;

    if (hasPeSection || hasLiteral)
      return new(true, this.Id, hasPeSection && hasLiteral ? 1.0 : 0.85, []);

    return new(false, this.Id, 0, [
      new(ExecutableDiagnosticCode.NotPackedExecutable, "MPRESS: no .MPRESS section or MPRESS/MATCODE literal was found.", true),
    ]);
  }

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
        ["packer"] = "MPRESS",
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    if (packed.OriginalImage.LongLength > options.MaximumInputSize)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, "Input exceeds configured executable unpacking size limit.", true),
      ]);

    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var payloads = LocatePayloads(packed.OriginalImage);
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect;

    if (payloads.Count == 1) {
      artifacts.Add(new("compressed_payload.bin", payloads[0].Data, "mpress"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
    } else if (payloads.Count > 1) {
      for (var i = 0; i < payloads.Count; i++)
        artifacts.Add(new($"payload_candidates/candidate_{i:000}_{Sanitize(payloads[i].Name)}.bin", payloads[i].Data, "mpress"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
    } else {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "MPRESS was detected, but no packed section payload could be carved.", true));
    }

    if (level == ExecutableUnpackLevel.PayloadLocated)
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        "MPRESS .MPRESS1 payload located. Remaining transform: MPRESS uses a non-standard LZMA variant " +
        "(custom range-coder initialization, not bit-compatible with the standard LZMA building block) " +
        "followed by an E8/E9 x86 (BCJ) call/jump filter; neither is reversible with the current building blocks.",
        true));

    caps |= packed.ImageInfo?.Container switch {
      ExecutableContainerKind.Pe => ExecutableUnpackCapabilities.SupportsPe,
      ExecutableContainerKind.Elf => ExecutableUnpackCapabilities.SupportsElf,
      _ => ExecutableUnpackCapabilities.None,
    };
    caps |= packed.ImageInfo?.Architecture switch {
      CpuArchitecture.X86 => ExecutableUnpackCapabilities.SupportsX86,
      CpuArchitecture.X64 => ExecutableUnpackCapabilities.SupportsX64,
      _ => ExecutableUnpackCapabilities.None,
    };

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private readonly record struct Payload(string Name, byte[] Data);

  private static List<Payload> LocatePayloads(byte[] image) {
    var payloads = new List<Payload>();
    if (PackerScanner.IsPe(image)) {
      foreach (var s in PackerScanner.GetPeSectionRanges(image)) {
        if (!s.Name.StartsWith(".MPRESS", StringComparison.OrdinalIgnoreCase))
          continue;
        if (s.RawSize <= 0 || s.RawOffset >= image.Length)
          continue;
        var length = (int)Math.Min(s.RawSize, (uint)(image.Length - s.RawOffset));
        payloads.Add(new(s.Name, image.AsSpan((int)s.RawOffset, length).ToArray()));
      }
    }
    return payloads;
  }

  private static string Sanitize(string value) {
    var sb = new StringBuilder(value.Length);
    foreach (var c in value)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.Length == 0 ? "payload" : sb.ToString();
  }

  private static byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"mpress\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"compressionCore\": \"mpress-transform\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
