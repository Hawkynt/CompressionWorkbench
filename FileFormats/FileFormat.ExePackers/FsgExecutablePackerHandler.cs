#pragma warning disable CS1591
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Real unpack handler for FSG ("Fast, Small, Good") — bart/Xtreeme's minimal
/// aPLib-based Win32 PE compressor (v1.x–2.0, ~2004), identified by the
/// <c>"FSG!"</c> marker its ~158-byte stub embeds near the entry point. FSG chose
/// aPLib specifically (LZMA "too big", NRV "too slow"); the shared
/// <see cref="AplibSectionPackerHandler"/> carves the aPLib-compressed section
/// and inflates it.
/// </summary>
public sealed class FsgExecutablePackerHandler : AplibSectionPackerHandler {
  public override string Id => "fsg";
  public override string DisplayName => "FSG (Fast Small Good) aPLib-packed PE";
  protected override string PackerLabel => "FSG";

  private static ReadOnlySpan<byte> FsgMagic => "FSG!"u8;

  /// <summary>
  /// Walks FSG's own block list first — the packer concatenates one bare aPLib
  /// stream per original section and only the entry-point stub says where they
  /// start, which no amount of scanning section boundaries will find. Images
  /// whose stub is not the shape <see cref="FsgImage"/> models fall through to
  /// the shared aPLib scan.
  /// </summary>
  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    if (!FsgImage.TryRead(packed.OriginalImage, options.MaximumDecompressedSize, out var blocks))
      return base.Unpack(packed, options);

    var payload = FsgImage.Assemble(packed.OriginalImage, blocks);
    if (payload.Length == 0)
      return base.Unpack(packed, options);

    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
      new("decompressed_payload.bin", payload, "fsg-aplib"),
    };

    var caps = ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.CanDecompressPayload |
      ExecutableUnpackCapabilities.SupportsPe |
      ExecutableUnpackCapabilities.SupportsX86;

    var level = ExecutableUnpackLevel.PayloadDecompressed;
    if (packed.ImageInfo is { Container: ExecutableContainerKind.Pe } info) {
      try {
        artifacts.Add(new("reconstructed/reconstructed.exe", PeRebuilder.RebuildSynthetic(info, payload), "stored"));
        level = ExecutableUnpackLevel.RebuiltExecutable;
        caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
      } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) {
        // A synthetic rebuild is a bonus; the decoded blocks stand on their own.
      }
    }

    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        $"FSG payload decoded: {blocks.Count} aPLib block(s) placed at RVA " +
        string.Join(", ", blocks.Select(b => $"0x{b.Rva:X}")) + ".",
        false),
      new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        "FSG discards the original PE headers and rebuilds imports from its own tables; the decoded image is the mapped section content, not a byte-identical copy of the input file.",
        false),
    };

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  protected override (bool Match, double Confidence, string Reason) DetectPe(ReadOnlySpan<byte> image) {
    var idx = PackerScanner.IndexOfBounded(image, FsgMagic, 0x4000);
    if (idx >= 0)
      return (true, 1.0, "");

    var sections = PackerScanner.GetPeSections(image);
    var emptyCount = sections.Count(s => string.IsNullOrWhiteSpace(s.Name));
    var hasFsgLayout = sections.Count == 3 &&
      (sections[0].Name.Equals("t", StringComparison.OrdinalIgnoreCase) || emptyCount >= 2) &&
      sections.Skip(1).Any(s => s.Name.Equals("ta", StringComparison.OrdinalIgnoreCase)) &&
      sections.Skip(1).Any(s => s.Name.Equals("a", StringComparison.OrdinalIgnoreCase));
    var hasBlankFsgLayout = sections.Count == 3 && emptyCount >= 2;
    return hasFsgLayout || hasBlankFsgLayout
      ? (true, 0.9, "")
      : (false, 0, "FSG: neither \"FSG!\" marker nor t/ta/a section layout found.");
  }
}

/// <summary>
/// Real unpack handler for PECompact 2 (Bitsum) — an aPLib-capable Win32 PE
/// compressor whose stub carries a <c>"PEC2"</c> marker and typically renames
/// sections to <c>.pec1</c>/<c>.pec2</c>. The shared base attempts an aPLib decode
/// of the packed section (PECompact also supports other codecs via plug-ins, in
/// which case the handler reports the payload as located but not aPLib-decodable).
/// </summary>
public sealed class PeCompactExecutablePackerHandler : AplibSectionPackerHandler {
  public override string Id => "pecompact";
  public override string DisplayName => "PECompact aPLib-packed PE";
  protected override string PackerLabel => "PECompact";

  protected override (bool Match, double Confidence, string Reason) DetectPe(ReadOnlySpan<byte> image) {
    var hasMarker = PackerScanner.IndexOfBounded(image, "PEC2"u8, 0x10000) >= 0
      || PackerScanner.IndexOfBounded(image, "PECompact2"u8, 0x10000) >= 0;
    var hasSection = PackerScanner.GetPeSections(image).Any(s => s.Name.StartsWith(".pec", StringComparison.OrdinalIgnoreCase));
    if (hasMarker || hasSection)
      return (true, hasMarker && hasSection ? 1.0 : 0.85, "");
    return (false, 0, "PECompact: no 'PEC2' marker or .pec section found.");
  }
}

/// <summary>
/// Real unpack handler for Packman - a Win32 PE compressor that marks its
/// payload section as <c>.PACKMAN</c>. The shared aPLib base handles the
/// corpus variant whose section contains a clean aPLib stream.
/// </summary>
public sealed class PackmanExecutablePackerHandler : AplibSectionPackerHandler {
  public override string Id => "packman";
  public override string DisplayName => "Packman aPLib-packed PE";
  protected override string PackerLabel => "Packman";

  protected override (bool Match, double Confidence, string Reason) DetectPe(ReadOnlySpan<byte> image) {
    var hasLiteral = PackerScanner.IndexOfBounded(image, "PACKMAN"u8, 0x10000) >= 0 ||
      PackerScanner.IndexOfBounded(image, "Packman"u8, 0x10000) >= 0;
    var hasSection = PackerScanner.GetPeSections(image).Any(s =>
      s.Name.Equals(".PACKMAN", StringComparison.OrdinalIgnoreCase));
    if (hasLiteral || hasSection)
      return (true, hasLiteral && hasSection ? 1.0 : 0.85, "");
    return (false, 0, "Packman: no 'PACKMAN' literal or .PACKMAN section found.");
  }
}

/// <summary>
/// Real unpack handler for Enigma Virtual Box corpus outputs. EVB is primarily
/// a file bundler, but the public Packing Box PE corpus variants include
/// <c>.enigma1</c>/<c>.enigma2</c> sections whose payload is recovered by the
/// shared managed aPLib PE pipeline. Full bundled file-tree extraction remains a
/// separate higher-level target.
/// </summary>
public sealed class EnigmaVirtualBoxExecutablePackerHandler : AplibSectionPackerHandler {
  public override string Id => "enigmavirtualbox";
  public override string DisplayName => "Enigma Virtual Box aPLib-packed PE";
  protected override string PackerLabel => "Enigma Virtual Box";

  protected override (bool Match, double Confidence, string Reason) DetectPe(ReadOnlySpan<byte> image) {
    var sections = PackerScanner.GetPeSections(image);
    var hasEnigma1 = sections.Any(s => s.Name.Equals(".enigma1", StringComparison.OrdinalIgnoreCase));
    var hasEnigma2 = sections.Any(s => s.Name.Equals(".enigma2", StringComparison.OrdinalIgnoreCase));
    var hasLiteral = PackerScanner.IndexOfBounded(image, "VirtualBox"u8, 0x200000) >= 0 ||
      PackerScanner.IndexOfBounded(image, "EVB"u8, 0x200000) >= 0 ||
      PackerScanner.IndexOfBounded(image, "enigma"u8, 0x200000) >= 0;
    if (hasEnigma1 && hasEnigma2)
      return (true, hasLiteral ? 1.0 : 0.92, "");
    if (hasEnigma2 && hasLiteral)
      return (true, 0.88, "");
    return (false, 0, "Enigma Virtual Box: .enigma1/.enigma2 section pair not found.");
  }
}

/// <summary>
/// Real unpack handler for PE-Toy, a Win32 PE packer whose documented shell
/// layout adds a <c>.petoy</c> section and uses an aPLib payload. The shared
/// aPLib base carves and inflates the payload and emits a synthetic rebuilt PE
/// when the decoded image can be mapped.
/// </summary>
public sealed class PeToyExecutablePackerHandler : AplibSectionPackerHandler {
  public override string Id => "petoy";
  public override string DisplayName => "PE-Toy aPLib-packed PE";
  protected override string PackerLabel => "PE-Toy";

  protected override (bool Match, double Confidence, string Reason) DetectPe(ReadOnlySpan<byte> image) {
    var hasSection = PackerScanner.GetPeSections(image).Any(s =>
      s.Name.Equals(".petoy", StringComparison.OrdinalIgnoreCase));
    var hasLiteral = PackerScanner.IndexOfBounded(image, "petoy"u8, 0x10000) >= 0 ||
      PackerScanner.IndexOfBounded(image, "PE Toy"u8, 0x10000) >= 0;
    if (hasSection || hasLiteral)
      return (true, hasSection && hasLiteral ? 1.0 : 0.9, "");
    return (false, 0, "PE-Toy: no .petoy section or PE-Toy literal found.");
  }
}

/// <summary>
/// Generic fallback for aPLib-compressed PEs whose specific packer we don't
/// name (JDPack and other aPLib-family stubs, or aPLib output from an unknown
/// tool). Detection is by decode: a PE section that inflates to a
/// cleanly-terminated, expanding aPLib stream is accepted. Registered last and
/// at low confidence so a recognized packer always wins when its marker is
/// present.
/// </summary>
public sealed class GenericAplibPackedPeHandler : AplibSectionPackerHandler {
  public override string Id => "aplib_pe";
  public override string DisplayName => "aPLib-packed PE (generic)";
  protected override string PackerLabel => "aPLib-packed PE";

  private const long DetectDecodeCap = 64L * 1024 * 1024;

  protected override (bool Match, double Confidence, string Reason) DetectPe(ReadOnlySpan<byte> image) =>
    TryFindAplibPayload(image.ToArray(), DetectDecodeCap, out _)
      ? (true, 0.45, "")
      : (false, 0, "No PE section inflated to a cleanly-terminated aPLib stream.");
}
