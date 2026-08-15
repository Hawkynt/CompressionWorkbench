#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Real unpack handler for ASPack (Solodovnikov, 1998+), the long-running Win32 PE
/// compressor whose stub renames a section pair to <c>.aspack</c> / <c>.adata</c> and
/// embeds the literal <c>"ASPack"</c> near the start of the file.
/// </summary>
/// <remarks>
/// <para>
/// ASPack's compression core is not aPLib, contrary to a widespread claim: it is an
/// LZX-family LZ77 with per-block canonical Huffman codes, decoded by
/// <see cref="AsPackLzDecoder"/>. The stub keeps the original section layout and
/// replaces each section's raw bytes in place, so unpacking means walking the stub's
/// region table (<see cref="AsPackImage"/>), decoding every region and reversing the
/// packer's <c>E8</c>/<c>E9</c> call filter.
/// </para>
/// <para>
/// Builds we do not model fall back to the shared aPLib section scan, which still
/// covers the handful of ASPack-branded images that really do carry a bare aPLib
/// stream.
/// </para>
/// </remarks>
public sealed class AsPackExecutablePackerHandler : AplibSectionPackerHandler {
  public override string Id => "aspack";
  public override string DisplayName => "ASPack (Win32 PE)";
  protected override string PackerLabel => "ASPack";

  protected override (bool Match, double Confidence, string Reason) DetectPe(ReadOnlySpan<byte> image) {
    var sections = PackerScanner.GetPeSections(image);
    var hasSection = sections.Any(s =>
      s.Name.Equals(".aspack", StringComparison.OrdinalIgnoreCase) ||
      s.Name.Equals(".adata", StringComparison.OrdinalIgnoreCase));
    var hasLiteral = PackerScanner.IndexOfBounded(image, "ASPack"u8, 0x10000) >= 0;
    if (hasSection || hasLiteral)
      return (true, hasSection && hasLiteral ? 1.0 : 0.85, "");
    return (false, 0, "ASPack: neither .aspack/.adata section nor 'ASPack' literal found.");
  }

  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    if (packed.OriginalImage.LongLength > options.MaximumInputSize)
      return base.Unpack(packed, options);
    if (!AsPackImage.TryRead(packed.OriginalImage, packed.ImageInfo, out var layout)
        || layout is null || packed.ImageInfo is null)
      return base.Unpack(packed, options);

    var info = packed.ImageInfo;
    var diagnostics = new List<ExecutableDiagnostic>();
    var restored = new List<(AsPackRegion Region, byte[] Data)>();
    var stored = 0;
    foreach (var region in layout.Regions) {
      if (region.IsStored) {
        ++stored;
        continue;
      }

      try {
        var data = AsPackImage.Restore(packed.OriginalImage, info, layout, region, options.MaximumDecompressedSize);
        if (data is { Length: > 0 })
          restored.Add((region, data));
        else
          diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
            $"ASPack region at RVA 0x{region.Rva:X8} has no data in the file."));
      } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException) {
        diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
          $"ASPack region at RVA 0x{region.Rva:X8} ({region.OriginalSize} bytes) failed to decode: {ex.Message}", true));
      }
    }

    if (restored.Count == 0)
      return base.Unpack(packed, options);

    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadata(packed, layout, restored.Count, stored), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };

    var total = 0L;
    foreach (var (region, data) in restored) {
      var name = AsPackImage.DescribeRva(info, region.Rva).Replace('/', '_').Replace('\\', '_');
      artifacts.Add(new($"sections/{name}@0x{region.Rva:X8}.bin", data, "aspack-lz"));
      total += data.Length;
    }

    var payload = new byte[total];
    var written = 0;
    foreach (var (_, data) in restored) {
      data.CopyTo(payload, written);
      written += data.Length;
    }

    artifacts.Add(new("decompressed_payload.bin", payload, "stored"));

    diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
      $"ASPack: {restored.Count} region(s) decoded, {stored} stored region(s) left in place" +
      (layout.OriginalEntryPointRva is { } oep ? $", original entry point RVA 0x{oep:X8}" : "") + "."));
    if (!layout.CallFilterEnabled)
      diagnostics.Add(new(ExecutableDiagnosticCode.TransformNotReversible,
        "ASPack: this image was packed with the E8/E9 call filter disabled; region bytes are used verbatim."));
    diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
      "ASPack relocates resource data into its stub section and zeroes the original .rsrc bytes before " +
      "compressing, and rewrites the import directory, so a runnable rebuild needs both directories " +
      "reconstructed on top of the decoded regions."));

    var caps = ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.CanDecompressPayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (info.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (info.Architecture == CpuArchitecture.X64)
      caps |= ExecutableUnpackCapabilities.SupportsX64;

    var result = new UnpackResult(ExecutableUnpackLevel.PayloadDecompressed, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, info, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static byte[] BuildMetadata(PackedExecutable packed, AsPackLayout layout, int decoded, int stored) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"aspack\",\n");
    sb.Append("  \"compressionCore\": \"aspack-lz\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"stubRva\": \"0x{layout.StubRva:X8}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"regionTableFileOffset\": \"0x{layout.RegionTableFileOffset:X}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"regionsDecoded\": {decoded},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"regionsStored\": {stored},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"callFilter\": \"{(layout.CallFilterEnabled ? layout.CallFilterWide ? "wide" : $"marked:0x{layout.CallFilterMarker:X2}" : "disabled")}\",\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"  \"originalEntryPointRva\": {(layout.OriginalEntryPointRva is { } oep ? $"\"0x{oep:X8}\"" : "null")},\n");
    sb.Append("  \"regions\": [\n");
    for (var i = 0; i < layout.Regions.Count; ++i) {
      var region = layout.Regions[i];
      sb.Append(CultureInfo.InvariantCulture,
        $"    {{ \"rva\": \"0x{region.Rva:X8}\", \"originalSize\": {region.OriginalSize}, \"characteristics\": \"0x{region.Characteristics:X8}\", \"stored\": {(region.IsStored ? "true" : "false")} }}");
      sb.Append(i + 1 < layout.Regions.Count ? ",\n" : "\n");
    }

    sb.Append("  ]\n}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
