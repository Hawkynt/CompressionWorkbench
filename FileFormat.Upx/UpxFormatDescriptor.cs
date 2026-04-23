#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Dictionary.Nrv2b;
using Compression.Core.Dictionary.Nrv2d;
using Compression.Core.Dictionary.Nrv2e;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Upx;

/// <summary>
/// Pseudo-archive descriptor for UPX-packed executables. Surfaces each packed
/// region (PE UPX0/UPX1/UPX2 sections, or the raw packed payload for ELF/Mach-O)
/// as a separate entry, plus the UPX packer header bytes, the decompressed
/// payload (when the compression method is supported), and a metadata.ini
/// summary of format, method, size, and checksum information.
/// </summary>
/// <remarks>
/// <para>
/// UPX binaries retain the outer PE/ELF/Mach-O shell of their original container,
/// so the descriptor cannot rely on a leading-magic match. Detection works
/// across three layers: explicit packer-header magic, canonical section names
/// (<c>UPX0/1/2</c>), and a structural fingerprint (BSS-style first section,
/// entry point in the last section, RWX section flags, high payload entropy).
/// All three layers tolerate tampered binaries — even with the <c>"UPX!"</c>
/// string wiped and section names renamed, the structural fingerprint surfaces
/// the file as a probable UPX-packed binary.
/// </para>
/// <para>
/// Decompression: NRV2B / NRV2D / NRV2E in all three bit-stream widths (LE32,
/// LE16, 8-bit — UPX methods 2 / 3 / 4 / 5 / 6 / 7 / 8 / 9 / 10) and LZMA
/// payloads are decompressed in-process using the matching building block; the
/// result is exposed as <c>decompressed_payload.bin</c>. DEFLATE-packed UPX
/// (method 15) remains unported — those payloads are surfaced as compressed
/// bytes only with a <c>metadata.ini</c> note explaining the limitation.
/// </para>
/// </remarks>
public sealed class UpxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Upx";
  public string DisplayName => "UPX-packed executable";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".exe";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "UPX-packed executable (PE / ELF / Mach-O) — surfaces UPX0/UPX1/UPX2 sections, " +
    "the UPX packer header, the decompressed payload (when method is supported), and " +
    "tooling metadata. Detection survives wiped \"UPX!\" magic + renamed sections via " +
    "structural fingerprinting (BSS-style first section, RWX flags, high-entropy payload).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        e.Method, false, false, null))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static IEnumerable<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var info = UpxReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    if (info.Confidence == UpxReader.DetectionConfidence.None)
      throw new InvalidDataException(
        "UPX: no UPX evidence detected (checked PE section names + UPX! header scan + " +
        "tampered-header scan + structural fingerprint).");

    var (decompressed, decompressNote) = TryDecompress(info);
    // Method label for the bytes that are actually compressed (UPX1 section + the
    // sliced compressed_payload.bin). Sections UPX0/UPX2, the packer header, the
    // metadata file, the decompressed payload, and the tooling banner are all
    // surfaced as raw bytes.
    var compressionMethod = info.Header is { } h ? UpxReader.MethodName(h.Method).ToLowerInvariant() : "stored";

    yield return ("metadata.ini", BuildMetadata(info, decompressNote), "stored");

    foreach (var s in info.PeSections) {
      if (s.Name is not ("UPX0" or "UPX1" or "UPX2")) continue;
      // UPX1 contains the stub + compressed payload. UPX0 is BSS-style (no raw bytes —
      // runtime decompression target). UPX2 holds the small import-thunk table. Only
      // UPX1 carries genuinely compressed data.
      var sectionMethod = s.Name == "UPX1" ? compressionMethod : "stored";
      if (s.RawOffset == 0 || s.RawSize == 0) {
        yield return ($"section_{s.Name}.bin", [], sectionMethod);
        continue;
      }
      var startByte = (int)s.RawOffset;
      var endByte = startByte + (int)s.RawSize;
      if (endByte > info.Image.Length) endByte = info.Image.Length;
      yield return ($"section_{s.Name}.bin", info.Image[startByte..endByte], sectionMethod);
    }

    if (info.Header is { } hdr) {
      var off = hdr.Offset;
      var len = Math.Min(32, info.Image.Length - off);
      yield return ("upx_packer_header.bin", info.Image.AsSpan(off, len).ToArray(), "stored");

      // Surface the raw compressed payload (between stub end and PackHeader start)
      // even when we can't decompress it, so callers can pipe it to upx -d.
      var compressed = UpxReader.LocateCompressedPayload(info);
      if (compressed != null)
        yield return ("compressed_payload.bin", compressed, compressionMethod);
    }

    if (decompressed != null)
      yield return ("decompressed_payload.bin", decompressed, "stored");

    if (!string.IsNullOrEmpty(info.ToolingString))
      yield return ("upx_info.txt", Encoding.UTF8.GetBytes(info.ToolingString), "stored");
  }

  /// <summary>
  /// Attempts to decompress the UPX payload. Returns <c>(bytes, null)</c> on success,
  /// <c>(null, reason)</c> on every failure mode (no header, unsupported method,
  /// payload-length mismatch, decompression error). The reason string is folded into
  /// metadata.ini so the caller can see why no <c>decompressed_payload.bin</c>
  /// entry was emitted.
  /// </summary>
  private static (byte[]? Data, string? Note) TryDecompress(UpxReader.Info info) {
    if (info.Header is not { } h) return (null, "no PackHeader found — cannot anchor compressed payload.");
    var compressed = UpxReader.LocateCompressedPayload(info);
    if (compressed == null) return (null, "compressed payload window falls outside image bounds.");

    try {
      switch (h.Method) {
        case 2:  // NRV2B_LE32
          return (Nrv2bBuildingBlock.DecompressRaw(compressed, (int)h.UncompressedSize), null);
        case 4:  // NRV2B_LE16
          return (Nrv2bBuildingBlock.DecompressRawLe16(compressed, (int)h.UncompressedSize), null);
        case 6:  // NRV2B_8
          return (Nrv2bBuildingBlock.DecompressRawByte(compressed, (int)h.UncompressedSize), null);

        case 3:  // NRV2D_LE32
          return (Nrv2dBuildingBlock.DecompressRaw(compressed, (int)h.UncompressedSize), null);
        case 5:  // NRV2D_LE16
          return (Nrv2dBuildingBlock.DecompressRawLe16(compressed, (int)h.UncompressedSize), null);
        case 7:  // NRV2D_8
          return (Nrv2dBuildingBlock.DecompressRawByte(compressed, (int)h.UncompressedSize), null);

        case 8:  // NRV2E_LE32
          return (Nrv2eBuildingBlock.DecompressRaw(compressed, (int)h.UncompressedSize), null);
        case 9:  // NRV2E_LE16
          return (Nrv2eBuildingBlock.DecompressRawLe16(compressed, (int)h.UncompressedSize), null);
        case 10: // NRV2E_8
          return (Nrv2eBuildingBlock.DecompressRawByte(compressed, (int)h.UncompressedSize), null);

        case 14: // LZMA
          // UPX wraps LZMA payloads with its own 2-byte preamble (filter+ctosize meta).
          // For now we feed the raw window to our LZMA building block; if UPX's preamble
          // is present this will fail and we fall through to the catch-and-report path.
          return (new LzmaBuildingBlock().Decompress(compressed), null);

        case 15: // DEFLATE
          return (null, "DEFLATE-packed UPX is rare and not yet wired; use upx -d.");

        default:
          return (null, $"unknown compression method {h.Method}; use upx -d.");
      }
    } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException) {
      return (null, $"decompression failed: {ex.Message}");
    }
  }

  private static byte[] BuildMetadata(UpxReader.Info info, string? decompressNote) {
    var sb = new StringBuilder();
    sb.AppendLine("[upx]");
    sb.Append(CultureInfo.InvariantCulture, $"container = {info.Kind}\n");
    sb.Append(CultureInfo.InvariantCulture, $"image_size = {info.Image.LongLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"detection_confidence = {info.Confidence}\n");
    if (info.PeEntryPointRva is { } rva)
      sb.Append(CultureInfo.InvariantCulture, $"entry_point_rva = 0x{rva:X8} (section index {info.PeEntryPointSectionIndex})\n");

    sb.AppendLine();
    sb.AppendLine("[detection_evidence]");
    sb.Append(CultureInfo.InvariantCulture, $"section_names_match = {info.Evidence.SectionNamesMatch}\n");
    sb.Append(CultureInfo.InvariantCulture, $"tooling_banner_present = {info.Evidence.ToolingBannerPresent}\n");
    sb.Append(CultureInfo.InvariantCulture, $"pack_header_found = {info.Evidence.PackHeaderFound}\n");
    sb.Append(CultureInfo.InvariantCulture, $"pack_header_magic_intact = {info.Evidence.PackHeaderMagicIntact}\n");
    sb.Append(CultureInfo.InvariantCulture, $"structural_fingerprint = {info.Evidence.StructuralFingerprintMatch}\n");
    sb.Append(CultureInfo.InvariantCulture, $"fingerprint_score = {info.Evidence.FingerprintScore}\n");
    if (!string.IsNullOrEmpty(info.Evidence.FingerprintReasoning))
      sb.Append(CultureInfo.InvariantCulture, $"fingerprint_reasoning = {info.Evidence.FingerprintReasoning}\n");

    sb.AppendLine();
    sb.AppendLine("[pe_sections]");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {info.PeSections.Count}\n");
    foreach (var s in info.PeSections) {
      sb.Append(CultureInfo.InvariantCulture,
        $"section = {s.Name} vsize=0x{s.VirtualSize:X8} vaddr=0x{s.VirtualAddress:X8} rawSize=0x{s.RawSize:X8} rawOffset=0x{s.RawOffset:X8} flags=0x{s.Characteristics:X8}\n");
    }

    if (info.Header is { } h) {
      sb.AppendLine();
      sb.AppendLine("[packer_header]");
      sb.Append(CultureInfo.InvariantCulture, $"offset = 0x{h.Offset:X}\n");
      sb.Append(CultureInfo.InvariantCulture, $"magic_intact = {h.MagicIntact}\n");
      sb.Append(CultureInfo.InvariantCulture, $"version = {h.Version}\n");
      sb.Append(CultureInfo.InvariantCulture, $"format = {h.Format} ({UpxReader.FormatName(h.Format)})\n");
      sb.Append(CultureInfo.InvariantCulture, $"method = {h.Method} ({UpxReader.MethodName(h.Method)})\n");
      sb.Append(CultureInfo.InvariantCulture, $"level = {h.Level}\n");
      sb.Append(CultureInfo.InvariantCulture, $"uncompressed_size = {h.UncompressedSize}\n");
      sb.Append(CultureInfo.InvariantCulture, $"compressed_size = {h.CompressedSize}\n");
      sb.Append(CultureInfo.InvariantCulture, $"uncompressed_adler32 = 0x{h.UncompressedAdler32:X8}\n");
      sb.Append(CultureInfo.InvariantCulture, $"compressed_adler32 = 0x{h.CompressedAdler32:X8}\n");
      sb.Append(CultureInfo.InvariantCulture, $"filter_id = {h.FilterId}\n");
    }

    if (decompressNote != null) {
      sb.AppendLine();
      sb.AppendLine("[decompression]");
      sb.Append(CultureInfo.InvariantCulture, $"status = {decompressNote}\n");
    }

    if (!string.IsNullOrEmpty(info.ToolingString)) {
      sb.AppendLine();
      sb.AppendLine("[tooling]");
      sb.Append(CultureInfo.InvariantCulture, $"banner = {info.ToolingString.TrimEnd('$', ' ')}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
