#pragma warning disable CS1591
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Detector/locator for SimpleDpack (github.com/YuriSizuku/SimpleDpack) — an
/// educational Win32/64 PE packer.
/// </summary>
/// <remarks>
/// <para>
/// The published master-branch source documents a fully static-decodable
/// container: an appended <c>".dpack"</c> section holding a shell-DLL
/// trailer followed by each compressed section as
/// <c>DLZMA_HEADER { size_t RawDataSize, DataSize; char LzmaProps[5]; }</c>
/// plus a raw LZMA stream, with per-section source/destination RVAs recorded
/// in a <c>DPACK_SHELL_INDEX</c> table exported by the packer's shell DLL
/// (<c>simpledpackshell.dll</c>) — <c>removeSectionDatas()</c> deliberately
/// keeps each stripped section's <c>VirtualAddress</c>/<c>VirtualSize</c>
/// header fields intact (only <c>SizeOfRawData</c> is zeroed), which is
/// exactly the "RawSize==0, VirtualSize&gt;0" pattern this codebase already
/// uses to locate similar stripped-section payloads.
/// </para>
/// <para>
/// This project downloaded and ran the actual published v0.5.3 release
/// binary (<c>SimpleDpack.exe</c> + <c>simpledpackshell.dll</c>) against a
/// real test executable to validate the format above before implementing a
/// decoder. The real output did not match the documented behavior: nearly
/// every section's raw data was left byte-identical to the input (only
/// <c>.text</c>/<c>.data</c>/<c>.idata</c> differed, and not by a size
/// consistent with LZMA compression), and the packed file grew rather than
/// shrank — i.e. the shipped release binary's actual section-stripping path
/// does not appear to engage the documented compression pipeline the same
/// way the reviewed master-branch source describes. Absent a source
/// revision that matches the released binary's observed behavior, hardcoding
/// a decoder against the documented (but unconfirmed-for-this-build) format
/// would be guessing, not decoding. This handler therefore stops at
/// <see cref="ExecutableUnpackLevel.PayloadLocated"/>: it detects the
/// packer and carves out the appended <c>".dpack"</c> blob (plus the
/// stripped-section RVA/size pairs it can already read straight from the
/// packed PE's own section table) for further analysis, without claiming a
/// decompression this project could not verify end-to-end.
/// </para>
/// </remarks>
public sealed class SimpleDpackExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "simpledpack";
  public string DisplayName => "SimpleDpack";

  private const string DpackSectionName = ".dpack";

  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "SimpleDpack: not a valid PE.", true)]);

    var hasSection = PackerScanner.GetPeSections(image).Any(s => s.Name == DpackSectionName);
    return hasSection
      ? new(true, this.Id, 0.85, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "SimpleDpack: no \".dpack\" section found.", true)]);
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
        ["packer"] = "SimpleDpack",
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    var diagnostics = new List<ExecutableDiagnostic>();
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", image, "stored"),
    };

    var ranges = PackerScanner.GetPeSectionRanges(image);
    var dpack = ranges.FirstOrDefault(s => s.Name == DpackSectionName);
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;

    if (dpack.Name != DpackSectionName || dpack.RawSize == 0) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "SimpleDpack: \".dpack\" section not found.", true));
    } else {
      var len = (int)Math.Min(dpack.RawSize, (uint)Math.Max(0, image.Length - dpack.RawOffset));
      artifacts.Add(new("dpack_section.bin", image.AsSpan((int)dpack.RawOffset, len).ToArray(), "stored"));

      // Sections dpack stripped keep their VirtualAddress/VirtualSize but
      // lose their raw file backing (FileSize == 0) — surface those as
      // located targets even though we can't yet decompress their content.
      var strippedTargets = (packed.ImageInfo?.Regions ?? [])
        .Where(r => r.Name != DpackSectionName && r.FileSize == 0 && r.VirtualSize > 0)
        .ToList();
      if (strippedTargets.Count > 0)
        artifacts.Add(new("stripped_sections.json", BuildStrippedSectionsJson(strippedTargets), "stored"));

      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        "SimpleDpack: \".dpack\" blob and stripped-section targets located. Full decode needs the per-section " +
        "DLZMA_HEADER/LZMA offsets recorded in the shell DLL's exported DPACK_SHELL_INDEX table; testing the " +
        "published v0.5.3 release against a real sample showed its actual output does not match the documented " +
        "master-branch container (sections were largely left unstripped), so this handler does not fabricate a " +
        "decode against an unconfirmed format.", true));
    }

    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static byte[] BuildStrippedSectionsJson(List<ExecutableRegion> targets) {
    var sb = new System.Text.StringBuilder();
    sb.Append("[\n");
    for (var i = 0; i < targets.Count; i++) {
      var t = targets[i];
      sb.Append(System.Globalization.CultureInfo.InvariantCulture,
        $"  {{ \"name\": \"{t.Name}\", \"virtualAddress\": \"0x{t.VirtualAddress:X}\", \"virtualSize\": {t.VirtualSize} }}");
      sb.Append(i < targets.Count - 1 ? ",\n" : "\n");
    }
    sb.Append("]\n");
    return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildMetadataJson(PackedExecutable packed) {
    var container = packed.ImageInfo?.Container.ToString().ToLowerInvariant() ?? "unknown";
    var architecture = packed.ImageInfo?.Architecture.ToString().ToLowerInvariant() ?? "unknown";
    return System.Text.Encoding.UTF8.GetBytes(
      "{\n" +
      "  \"packer\": \"simpledpack\",\n" +
      $"  \"container\": \"{container}\",\n" +
      $"  \"architecture\": \"{architecture}\",\n" +
      $"  \"imageSize\": {packed.OriginalImage.LongLength}\n" +
      "}\n");
  }
}
