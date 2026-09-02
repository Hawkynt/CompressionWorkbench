#pragma warning disable CS1591
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Detector/locator for PE-Packer (github.com/czs108/PE-Packer) — an
/// educational Win32 EXE packer.
/// </summary>
/// <remarks>
/// <para>
/// From the published source, the actual transforms are simple and fully
/// documented: <c>section.c</c> "encrypts" each encryptable section
/// (<c>.text</c>/<c>.data</c>/<c>.rdata</c>/<c>CODE</c>/<c>DATA</c>, up to
/// their trailing-zero-trimmed length) with a trivial additive cipher
/// (<c>base[i] += 0xCC</c>, i.e. reversible by subtracting 0xCC), and
/// <c>import_table.c</c> rewrites the import directory into a compact
/// custom format (no encryption — just a denser encoding: FirstThunk, a
/// length-prefixed DLL name, a function count, then each import as either a
/// length-prefixed name or an ordinal). Both blocks are copied into a new
/// <c>".shell"</c> section (added <em>after</em> all original section names
/// are cleared, which is why ".shell" survives as the only readable section
/// name) and are themselves additively "encrypted" the same way
/// (<c>InstallShell</c> calls <c>EncryptData</c> again on the whole load
/// segment + import table).
/// </para>
/// <para>
/// What blocks a full static decode: the byte ranges that were encrypted
/// (<c>ENCRY_INFO { DWORD rva; DWORD size; }</c> pairs) and the offsets of
/// the load segment / import table within the appended shell are recorded in
/// an <c>ORIGIN_PE_INFO</c> structure that only exists inside the compiled,
/// linked <c>.shell</c> section — built from MASM x86 assembly
/// (<c>entry_x86.asm</c>) whose exact byte layout is a property of that
/// specific compiled build, not of the documented C source. PE-Packer ships
/// no prebuilt release binary, and no MASM assembler (ml.exe) or MSVC
/// toolchain was available to reproduce an authoritative build and pin those
/// offsets empirically (the technique this project used successfully for
/// the Eronana packer, which has no assembly dependency). Reconstructing
/// those offsets from the raw bytes alone — without a known-good reference
/// build — would be guessing, not decoding, so this handler stops at
/// <see cref="ExecutableUnpackLevel.PayloadLocated"/>: it reliably detects
/// the packer and carves out the appended shell blob for further (manual or
/// tooled) analysis, but does not claim to reverse the encryption or import
/// rewrite.
/// </para>
/// </remarks>
public sealed class PePackerExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "pepacker_czs108";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PE-Packer (czs108)";

  private const string ShellSectionName = ".shell";

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
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "PE-Packer: not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    if (sections.Count == 0)
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "PE-Packer: no section table.", true)]);

    var shellIsLast = sections[^1].Name == ShellSectionName;
    if (!shellIsLast)
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "PE-Packer: last section is not \".shell\".", true)]);

    // ClearSectionNames() zeroes every original section's name before the
    // shell section is appended, so a genuine match also has every *other*
    // section name empty.
    var emptyNamedCount = sections.Take(sections.Count - 1).Count(s => s.Name.Length == 0);
    var confidence = emptyNamedCount == sections.Count - 1 ? 0.9 : 0.6;
    return new(true, this.Id, confidence, []);
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
        ["packer"] = "PE-Packer (czs108)",
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    var diagnostics = new List<ExecutableDiagnostic>();
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", image, "stored"),
    };

    var ranges = PackerScanner.GetPeSectionRanges(image);
    var shell = ranges.FirstOrDefault(s => s.Name == ShellSectionName);
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;

    if (shell.Name != ShellSectionName || shell.RawSize == 0) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "PE-Packer: \".shell\" section not found.", true));
    } else {
      var len = (int)Math.Min(shell.RawSize, (uint)Math.Max(0, image.Length - shell.RawOffset));
      var data = image.AsSpan((int)shell.RawOffset, len).ToArray();
      artifacts.Add(new("shell_section.bin", data, "stored"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        "PE-Packer: shell blob located. The additive (+0xCC) section \"encryption\" and compact import-table " +
        "rewrite are documented in the published source, but the byte ranges/offsets they apply to are recorded " +
        "in an ORIGIN_PE_INFO structure baked into the MASM-compiled shell (entry_x86.asm); no prebuilt release " +
        "binary exists and no MASM/MSVC toolchain was available to pin those offsets from a reference build, so " +
        "the transform is not reversed here.", true));
    }

    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static byte[] BuildMetadataJson(PackedExecutable packed) {
    var container = packed.ImageInfo?.Container.ToString().ToLowerInvariant() ?? "unknown";
    var architecture = packed.ImageInfo?.Architecture.ToString().ToLowerInvariant() ?? "unknown";
    return System.Text.Encoding.UTF8.GetBytes(
      "{\n" +
      "  \"packer\": \"pepacker_czs108\",\n" +
      $"  \"container\": \"{container}\",\n" +
      $"  \"architecture\": \"{architecture}\",\n" +
      $"  \"imageSize\": {packed.OriginalImage.LongLength}\n" +
      "}\n");
  }
}
