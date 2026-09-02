#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.Dictionary.Nrv2b;
using Compression.Core.Dictionary.Nrv2d;
using Compression.Core.Dictionary.Nrv2e;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Generic fallback for PE packers whose payload is a bare NRV stream inside a
/// section. This covers UPX-adjacent historical packers only when the payload is
/// actually recoverable; detection requires a successful inflate to avoid
/// promoting section-name heuristics into fake unpacking.
/// </summary>
public sealed class GenericNrvPackedPeHandler : IExecutablePackerHandler {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "nrv_pe";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Generic NRV-packed PE";

    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

    /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Generic NRV PE: not a valid PE.", true)]);

    var imageBytes = image.ToArray();
    if (TryFindNrvPayload(imageBytes, 16L * 1024 * 1024, out _))
      return new(true, this.Id, 0.35, []);

    return new(false, this.Id, 0, [
      new(ExecutableDiagnosticCode.PayloadNotFound, "No PE section decoded as a plausible bare NRV2B/NRV2D/NRV2E payload.", true),
    ]);
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
        ["packer"] = "Generic NRV PE",
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

    /// <summary>
  /// Performs the unpack operation.
  /// </summary>
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
    var level = ExecutableUnpackLevel.DetectionOnly;
    var rebuilt = false;

    var decoded = DecodeBest(CollectCandidates(packed.OriginalImage), options.MaximumDecompressedSize, diagnostics);
    if (decoded is { } best) {
      artifacts.Add(new($"nrv_payload@0x{best.Offset:X}_{best.Method}.bin", best.Compressed, best.Method));
      artifacts.Add(new("decompressed_payload.bin", best.Data, "stored"));
      level = ExecutableUnpackLevel.PayloadDecompressed;
      diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        "A bare NRV PE payload was decompressed. A runnable executable may still require packer-specific import and entry-point restoration."));

      if (packed.ImageInfo is { Container: ExecutableContainerKind.Pe } info)
        try {
          var pe = PeRebuilder.RebuildSynthetic(info, best.Data);
          artifacts.Add(new("reconstructed/reconstructed.exe", pe, "stored"));
          level = ExecutableUnpackLevel.RebuiltExecutable;
          rebuilt = true;
        } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) {
          diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed, $"PE reconstruction failed: {ex.Message}", options.StrictRebuild));
        }
    } else
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
        "No PE section decoded as a plausible bare NRV2B/NRV2D/NRV2E stream.", true));

    var caps = ExecutableUnpackCapabilities.CanDetect;
    if (level >= ExecutableUnpackLevel.PayloadDecompressed) {
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      caps |= ExecutableUnpackCapabilities.CanDecompressPayload;
    }
    if (rebuilt) caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
    caps |= ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private readonly record struct Candidate(int Offset, int ExpectedSize, bool TrustedVirtualTargetLayout, byte[] Bytes);
  private readonly record struct Decoded(int Offset, string Method, byte[] Compressed, byte[] Data);

  internal static bool TryFindNrvPayload(byte[] image, long maxDecompressed, out byte[] decoded) {
    var best = DecodeBest(CollectCandidates(image), maxDecompressed, null);
    decoded = best?.Data ?? [];
    return best is not null;
  }

  private static List<Candidate> CollectCandidates(byte[] image) {
    var candidates = new List<Candidate>();
    var sections = PackerScanner.GetPeSectionRanges(image);
    var virtualTarget = sections
      .Where(s => s.RawSize == 0 && s.VirtualSize > 0 && IsPlausibleNrvSectionName(s.Name))
      .OrderByDescending(s => s.VirtualSize)
      .FirstOrDefault();
    var expectedFromVirtualTarget = virtualTarget.VirtualSize > 0 && virtualTarget.VirtualSize <= 128u * 1024u * 1024u
      ? (int?)virtualTarget.VirtualSize
      : null;

    foreach (var s in sections) {
      if (s.RawSize <= 8 || s.RawOffset >= image.Length)
        continue;
      if (!IsPlausibleNrvSectionName(s.Name) && expectedFromVirtualTarget is null)
        continue;
      if (s.RawSize > 2u * 1024u * 1024u || s.VirtualSize > 32u * 1024u * 1024u)
        continue;
      var length = (int)Math.Min(s.RawSize, (uint)(image.Length - s.RawOffset));
      var expected = expectedFromVirtualTarget ??
        checked((int)Math.Min(Math.Max(s.VirtualSize, s.RawSize), 128u * 1024u * 1024u));
      if (expected <= length)
        expected = checked((int)Math.Min((long)length * 16, 128L * 1024 * 1024));
      candidates.Add(new((int)s.RawOffset, expected, expectedFromVirtualTarget is not null,
        image.AsSpan((int)s.RawOffset, length).ToArray()));
    }
    return candidates;
  }

  private static Decoded? DecodeBest(List<Candidate> candidates, long maxDecompressed, List<ExecutableDiagnostic>? diagnostics) {
    Decoded? best = null;
    foreach (var candidate in candidates) {
      var target = (int)Math.Min(candidate.ExpectedSize, maxDecompressed);
      if (target <= candidate.Bytes.Length || target < 64)
        continue;

      foreach (var start in StartOffsets(candidate.Bytes)) {
        var slice = candidate.Bytes.AsSpan(start);
        foreach (var method in Methods) {
          byte[] decoded;
          try {
            decoded = method.Decode(slice, target);
          } catch (InvalidDataException) {
            continue;
          } catch (IndexOutOfRangeException) {
            continue;
          }

          if (!candidate.TrustedVirtualTargetLayout && !LooksLikeRecoveredPayload(decoded, slice.Length))
            continue;

          if (best is null || decoded.Length > best.Value.Data.Length)
            best = new(candidate.Offset + start, method.Name, slice.ToArray(), decoded);
        }
      }
    }

    if (best is null && candidates.Count > 0)
      diagnostics?.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
        $"Tried {candidates.Count} PE section candidate(s); none decoded as a plausible NRV payload."));
    return best;
  }

  private delegate byte[] NrvDecoder(ReadOnlySpan<byte> compressed, int exactOutputSize);

  private static readonly (string Name, NrvDecoder Decode)[] Methods = [
    ("nrv2b", Nrv2bBuildingBlock.DecompressRaw),
    ("nrv2d", Nrv2dBuildingBlock.DecompressRaw),
    ("nrv2e", Nrv2eBuildingBlock.DecompressRaw),
  ];

  private static bool LooksLikeRecoveredPayload(byte[] decoded, int compressedLength) {
    if (decoded.Length < 64 || decoded.Length <= compressedLength)
      return false;
    if (decoded[0] == 'M' && decoded[1] == 'Z')
      return true;
    if (decoded[0] == 0x7F && decoded[1] == 'E' && decoded[2] == 'L' && decoded[3] == 'F')
      return true;
    var printable = 0;
    var sample = Math.Min(decoded.Length, 256);
    for (var i = 0; i < sample; i++)
      if (decoded[i] is >= 0x20 and <= 0x7E or 0x0A or 0x0D or 0x09)
        printable++;
    return printable >= sample * 3 / 4;
  }

  private static IEnumerable<int> StartOffsets(byte[] bytes) {
    var limit = Math.Min(bytes.Length - 8, 32);
    for (var i = 0; i <= limit; i++)
      yield return i;
  }

  private static bool IsPlausibleNrvSectionName(string name) =>
    name.Contains("nrv", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("upack", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("winupack", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("nsp", StringComparison.OrdinalIgnoreCase);

  private static byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"nrv_pe\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"compressionCore\": \"nrv2b/nrv2d/nrv2e\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
