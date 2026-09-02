#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.Dictionary.Aplib;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Shared base for Win32 PE packers whose compression core is aPLib
/// (<see cref="AplibBuildingBlock"/>) and which store the original image
/// aPLib-compressed inside one of the PE sections — the FSG / ASPack / PECompact
/// / RLPack family. Detection is packer-specific (section names, embedded
/// literals); recovery is shared: carve each section's raw bytes, attempt an
/// aPLib decode, and accept a candidate only when the stream terminates on a
/// genuine end-of-stream marker and expands, which rejects the false positives a
/// magic-less aPLib stream would otherwise invite.
/// </summary>
/// <remarks>
/// The emitted <c>decompressed_payload.bin</c> is the aPLib-inflated section
/// data. A natively-runnable rebuild additionally needs each packer's stub
/// replay (import reconstruction, original entry point), which is loader-version
/// specific and reported as a diagnostic rather than guaranteed — the same
/// honesty bar the UPX handler applies to its synthetic rebuild.
/// </remarks>
public abstract class AplibSectionPackerHandler : IExecutablePackerHandler {
    /// <summary>
  /// Gets the id.
  /// </summary>
public abstract string Id { get; }
    /// <summary>
  /// Gets the display name.
  /// </summary>
public abstract string DisplayName { get; }

  /// <summary>Display name of the packer as written into metadata (e.g. "FSG", "ASPack").</summary>
  protected abstract string PackerLabel { get; }

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
  /// Packer-specific detection. Returns the match confidence in [0,1] and, on no
  /// match, a human-readable reason. Implementations may assume the input is a
  /// valid PE (the base checks that first).
  /// </summary>
  protected abstract (bool Match, double Confidence, string Reason) DetectPe(ReadOnlySpan<byte> image);

    /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, $"{this.PackerLabel}: not a valid PE.", true)]);
    var (match, confidence, reason) = this.DetectPe(image);
    return match
      ? new(true, this.Id, confidence, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, reason, true)]);
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
        ["packer"] = this.PackerLabel,
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

    /// <summary>
  /// Performs the unpack operation.
  /// </summary>
public virtual UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    if (packed.OriginalImage.LongLength > options.MaximumInputSize)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, "Input exceeds configured executable unpacking size limit.", true),
      ]);

    var image = packed.OriginalImage;
    var diagnostics = new List<ExecutableDiagnostic>();
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", image, "stored"),
    };

    var candidates = CollectCandidates(image);
    var level = candidates.Count > 0 ? ExecutableUnpackLevel.PayloadLocated : ExecutableUnpackLevel.DetectionOnly;
    AddCandidateArtifacts(artifacts, candidates);

    var rebuilt = false;
    var best = DecodeBest(candidates, options.MaximumDecompressedSize, diagnostics);
    if (best is { } decoded) {
      artifacts.Add(new($"aplib_payload@0x{decoded.Offset:X}.bin", decoded.Compressed, "aplib"));
      artifacts.Add(new("decompressed_payload.bin", decoded.Data, "stored"));
      level = ExecutableUnpackLevel.PayloadDecompressed;
      diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        $"{this.PackerLabel} aPLib payload decompressed; a natively-runnable PE additionally needs the stub's import rebuild and original entry point, which are loader-version specific."));

      if (packed.ImageInfo is { Container: ExecutableContainerKind.Pe } info)
        try {
          var pe = PeRebuilder.RebuildSynthetic(info, decoded.Data);
          artifacts.Add(new("reconstructed/reconstructed.exe", pe, "stored"));
          level = ExecutableUnpackLevel.RebuiltExecutable;
          rebuilt = true;
        } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) {
          diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed, $"PE reconstruction failed: {ex.Message}", options.StrictRebuild));
        }
    } else if (candidates.Count > 0)
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
        $"{this.PackerLabel} detected but no PE section decoded as a cleanly-terminated aPLib stream (the payload may use a different codec or a transformed aPLib layout).", true));

    var caps = ExecutableUnpackCapabilities.CanDetect;
    if (level >= ExecutableUnpackLevel.PayloadLocated) caps |= ExecutableUnpackCapabilities.CanLocatePayload;
    if (level >= ExecutableUnpackLevel.PayloadDecompressed) caps |= ExecutableUnpackCapabilities.CanDecompressPayload;
    if (rebuilt) caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
    caps |= ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private readonly record struct Candidate(int Offset, int? ExpectedSize, byte[] Bytes);

    /// <summary>
  /// Represents a decoded.
  /// </summary>
protected readonly record struct Decoded(int Offset, byte[] Compressed, byte[] Data);

  private static List<Candidate> CollectCandidates(byte[] image) {
    var seen = new HashSet<int>();
    var candidates = new List<Candidate>();

    void Add(int offset, int length, int? expectedSize = null) {
      if (offset < 0 || length <= 4 || offset >= image.Length) return;
      length = Math.Min(length, image.Length - offset);
      if (!seen.Add(offset)) return;
      candidates.Add(new(offset, expectedSize, image.AsSpan(offset, length).ToArray()));
    }

    var ranges = PackerScanner.GetPeSectionRanges(image);
    var virtualTarget = ranges
      .Where(s => s.RawSize == 0 && s.VirtualSize > 0)
      .OrderByDescending(s => s.VirtualSize)
      .FirstOrDefault();
    var expectedFromVirtualTarget = virtualTarget.VirtualSize > 0 && virtualTarget.VirtualSize <= int.MaxValue
      ? (int?)virtualTarget.VirtualSize
      : null;

    foreach (var s in ranges)
      if (s.RawSize > 0) {
        var expected = IsPackedPayloadSection(s.Name) ? expectedFromVirtualTarget : null;
        Add((int)s.RawOffset, (int)s.RawSize, expected);
      }

    // Fall back to "everything after the PE headers" when the section table is
    // obliterated or the payload straddles section boundaries.
    if (ranges.Count == 0 && PackerScanner.IsPe(image))
      Add(0x40, image.Length - 0x40);

    return candidates;
  }

  /// <summary>
  /// Attempts to find and inflate a bare aPLib payload in any PE section of
  /// <paramref name="image"/>, returning the decoded original on success. The
  /// aPLib stream is self-validating (a genuine end-of-stream marker plus
  /// expansion), so this doubles as a reliable detector for aPLib-family packers
  /// whose specific marker we don't recognize. Shared by <see cref="Unpack"/> and
  /// the generic aPLib fallback handler.
  /// </summary>
  internal static bool TryFindAplibPayload(byte[] image, long maxDecompressed, out byte[] decoded) {
    var best = DecodeBest(CollectCandidates(image), maxDecompressed, null);
    decoded = best?.Data ?? [];
    return best is not null;
  }

  private static void AddCandidateArtifacts(List<UnpackArtifact> artifacts, List<Candidate> candidates) {
    if (candidates.Count == 0)
      return;

    if (candidates.Count == 1) {
      artifacts.Add(new("compressed_payload.bin", candidates[0].Bytes, "aplib-candidate"));
      return;
    }

    for (var i = 0; i < candidates.Count; i++)
      artifacts.Add(new($"payload_candidates/candidate_{i:000}@0x{candidates[i].Offset:X}.bin",
        candidates[i].Bytes, "aplib-candidate"));
  }

  private static Decoded? DecodeBest(List<Candidate> candidates, long maxDecompressed, List<ExecutableDiagnostic>? diagnostics) {
    Decoded? best = null;
    foreach (var candidate in candidates)
      foreach (var start in StartOffsets(candidate.Bytes)) {
        var slice = candidate.Bytes.AsSpan(start);
        var cap = candidate.ExpectedSize is { } expected
          ? expected
          : (int)Math.Min(maxDecompressed, (long)slice.Length * 64 + 0x10000);
        if (cap <= 0 || cap > maxDecompressed)
          continue;
        byte[] decoded;
        bool endMarkerHit;
        int consumed;
        try {
          decoded = AplibBuildingBlock.DecompressRaw(slice, cap, out endMarkerHit, out consumed);
        } catch (InvalidDataException) {
          continue;
        }

        // A real payload terminates on a genuine end-of-stream marker and expands
        // (packed < original). Trailing section padding after the marker is normal,
        // so we do not require consuming the whole section — only a substantial,
        // clean decode. Among qualifiers the largest decode wins, reliably picking
        // the true payload over any tiny coincidental end-marker.
        if (!endMarkerHit || decoded.Length < 64) continue;
        if (candidate.ExpectedSize is { } expectedSize && decoded.Length != expectedSize)
          continue;
        if (consumed < 16 || decoded.Length < consumed * 2) continue;

        if (best is null || decoded.Length > best.Value.Data.Length)
          best = new(candidate.Offset + start, candidate.Bytes.AsSpan(start, consumed).ToArray(), decoded);
      }

    if (best is null && candidates.Count > 0)
      diagnostics?.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
        $"Tried {candidates.Count} section candidate(s); none decoded as a cleanly-terminated aPLib stream."));
    return best;
  }

  /// <summary>
  /// Candidate start offsets for an aPLib stream inside a section. Real packers
  /// (FSG, RLPack, …) prefix the compressed data with a small zero-filled or
  /// parameter header, so the stream rarely begins at byte 0 of the section. We
  /// try: the section start, every small fixed offset, and each position right
  /// after a run of zero bytes — the layout a "header then aPLib body" produces —
  /// bounded so a single unpack stays fast (wrong starts fail almost instantly).
  /// </summary>
  private static IEnumerable<int> StartOffsets(byte[] bytes) {
    var starts = new SortedSet<int>();
    var limit = Math.Min(bytes.Length - 8, 4096);
    for (var i = 0; i < 32 && i < limit; i++) starts.Add(i);
    for (var i = 1; i <= limit; i++)
      if (bytes[i] != 0 && bytes[i - 1] == 0) {
        starts.Add(i);
        if (starts.Count >= 300) break;
      }
    return starts;
  }

  private static bool IsPackedPayloadSection(string name) =>
    name.Contains("pack", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("adata", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("aspack", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("pec", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("fsg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
  /// Performs the build metadata json operation.
  /// </summary>
protected byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"packer\": \"{this.Id}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"compressionCore\": \"aplib\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
