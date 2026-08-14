#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Dictionary.Nrv2b;
using Compression.Core.Dictionary.Nrv2d;
using Compression.Core.Dictionary.Nrv2e;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.Upx;

public sealed class UpxExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "upx";
  public string DisplayName => "UPX-packed executable";
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanBuildMemoryImage |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsMachO |
    ExecutableUnpackCapabilities.SupportsX86 |
    ExecutableUnpackCapabilities.SupportsX64 |
    ExecutableUnpackCapabilities.SupportsArm32 |
    ExecutableUnpackCapabilities.SupportsArm64;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var info = UpxReader.Read(image);
    var diagnostics = new List<ExecutableDiagnostic>();
    if (info.Confidence == UpxReader.DetectionConfidence.None)
      diagnostics.Add(new(ExecutableDiagnosticCode.NotPackedExecutable, "No UPX section, header, banner, or structural evidence was found.", true));
    else if (info.Confidence == UpxReader.DetectionConfidence.Heuristic)
      diagnostics.Add(new(ExecutableDiagnosticCode.NotPackedExecutable,
        "Only structural UPX-like evidence was found. The archive descriptor may surface this as a heuristic, but the executable-unpacking registry requires confirmed UPX evidence before routing the file to the UPX unpacker.",
        true));
    return new(info.Confidence == UpxReader.DetectionConfidence.Confirmed, this.Id, info.Confidence switch {
      UpxReader.DetectionConfidence.Confirmed => 1.0,
      UpxReader.DetectionConfidence.Heuristic => 0.25,
      _ => 0,
    }, diagnostics);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var containerInfo = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      containerInfo,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = "UPX",
        ["container"] = containerInfo.Container.ToString(),
        ["architecture"] = containerInfo.Architecture.ToString(),
      });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    if (packed.OriginalImage.LongLength > options.MaximumInputSize)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, "Input exceeds configured executable unpacking size limit.", true),
      ]);

    var info = UpxReader.Read(packed.OriginalImage);
    if (!info.IsUpxPacked)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.NotPackedExecutable, "Input is not UPX-packed.", true),
      ]);

    var diagnostics = new List<ExecutableDiagnostic>();
    var artifacts = new List<UnpackArtifact> {
      new("metadata.ini", BuildLegacyMetadata(info, null), "stored"),
      new("metadata.json", BuildMetadataJson(info, packed.ImageInfo), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    AddLegacySectionArtifacts(info, artifacts);

    var level = ExecutableUnpackLevel.DetectionOnly;
    byte[]? compressed = null;
    uint? headerlessExpectedSize = null;
    if (info.Header is { } hdr) {
      var headerLen = Math.Min(32, info.Image.Length - hdr.Offset);
      artifacts.Add(new("upx_packer_header.bin", info.Image.AsSpan(hdr.Offset, headerLen).ToArray(), "stored"));
      compressed = UpxReader.LocateCompressedPayload(info);
      if (compressed != null) {
        artifacts.Add(new("compressed_payload.bin", compressed, UpxReader.MethodName(hdr.Method).ToLowerInvariant()));
        level = ExecutableUnpackLevel.PayloadLocated;
      } else
        diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "UPX PackHeader was found, but the compressed payload window is outside the file.", true));
    } else {
      var headerless = TryLocateHeaderlessPePayload(info);
      if (headerless is { } h) {
        compressed = h.Payload;
        headerlessExpectedSize = h.ExpectedSize;
        artifacts.Add(new("compressed_payload.bin", compressed, "upx1-section"));
        level = ExecutableUnpackLevel.PayloadLocated;
        diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
          "No UPX PackHeader was found; compressed_payload.bin is the raw UPX1 section and decompression method must be inferred."));
      } else
        diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "No UPX PackHeader was found; payload location is heuristic-only."));
    }

    byte[]? decompressed = null;
    string? decompressionNote = null;
    if (info.Header is { } header && compressed != null) {
      (decompressed, decompressionNote) = TryDecompress(header, compressed, options);
      if (decompressed != null) {
        artifacts.Add(new("decompressed_payload.bin", decompressed, "stored"));
        level = ExecutableUnpackLevel.PayloadDecompressed;
      } else if (decompressionNote != null)
        diagnostics.Add(new(header.Method == 15 ? ExecutableDiagnosticCode.UnsupportedCompressionMethod : ExecutableDiagnosticCode.DecompressionFailed, decompressionNote, true));
    } else if (compressed != null && headerlessExpectedSize is { } expectedSize) {
      (decompressed, decompressionNote) = TryDecompressHeaderless(compressed, checked((int)expectedSize), options);
      if (decompressed != null) {
        artifacts.Add(new("decompressed_payload.bin", decompressed, "stored"));
        level = ExecutableUnpackLevel.PayloadDecompressed;
        diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
          $"UPX payload decompressed without a PackHeader by codec probing: {decompressionNote}."));
      } else if (decompressionNote != null)
        diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed, decompressionNote, true));
    }

    if (decompressed != null) {
      var transform = ReverseFilter(decompressed, info);
      artifacts.Add(new("filtered_payload.bin", transform.Payload, "stored"));
      diagnostics.AddRange(transform.Diagnostics);

      if (packed.ImageInfo is { } imageInfo) {
        var target = info.Kind == UpxReader.ContainerKind.Pe ? "UPX0" : null;
        var (memoryImage, regions, memoryDiagnostics) = ExecutableMemoryImageBuilder.Build(imageInfo, transform.Payload, target, options);
        diagnostics.AddRange(memoryDiagnostics);
        if (memoryImage != null) {
          artifacts.Add(new("memory_image.bin", memoryImage, "stored"));
          AddRegionArtifacts(artifacts, regions);
          level = ExecutableUnpackLevel.RuntimeMemoryImage;
        }

        if (imageInfo.Container == ExecutableContainerKind.Pe) {
          try {
            var rebuilt = PeRebuilder.RebuildSynthetic(imageInfo, transform.Payload);
            artifacts.Add(new("reconstructed/reconstructed.exe", rebuilt, "stored"));
            level = ExecutableUnpackLevel.RebuiltExecutable;
            diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
              "The PE rebuilder emitted a syntactically valid synthetic PE for static analysis; native loader runnability is not guaranteed."));
          } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) {
            diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed, $"PE reconstruction failed: {ex.Message}", options.StrictRebuild));
          }
        } else if (imageInfo.Container is ExecutableContainerKind.Elf or ExecutableContainerKind.MachO or ExecutableContainerKind.FatMachO) {
          diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed,
            $"{imageInfo.Container} rebuilding is staged after parser/payload support; decompressed payload and parsed metadata were emitted."));
        }
      }
    }

    if (!string.IsNullOrEmpty(info.ToolingString))
      artifacts.Add(new("upx_info.txt", Encoding.UTF8.GetBytes(info.ToolingString), "stored"));

    var result = new UnpackResult(level, CapabilityForLevel(level, packed.ImageInfo), artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  internal static UnpackResult Unpack(ReadOnlySpan<byte> image) {
    var handler = new UpxExecutablePackerHandler();
    var detection = handler.Detect(image);
    if (!detection.IsMatch)
      throw new InvalidDataException("UPX: no UPX evidence detected.");
    return handler.Unpack(handler.Parse(image, detection), new());
  }

  private static (byte[]? Data, string? Note) TryDecompress(UpxReader.PackerHeader h, byte[] compressed, UnpackOptions options) {
    if (h.UncompressedSize > options.MaximumDecompressedSize)
      return (null, "UPX uncompressed size exceeds configured executable unpacking limit.");

    if (h.Layout == UpxReader.PackerHeaderLayout.Legacy && h.CompressedAdler32 != 0) {
      var actualCompressedAdler = Adler32.Compute(compressed);
      if (actualCompressedAdler != h.CompressedAdler32)
        return (null,
          $"UPX compressed Adler-32 mismatch: expected 0x{h.CompressedAdler32:X8}, got 0x{actualCompressedAdler:X8}.");
    }

    try {
      var data = h.Method switch {
        2 => Nrv2bBuildingBlock.DecompressRaw(compressed, (int)h.UncompressedSize),
        4 => Nrv2bBuildingBlock.DecompressRawLe16(compressed, (int)h.UncompressedSize),
        6 => Nrv2bBuildingBlock.DecompressRawByte(compressed, (int)h.UncompressedSize),
        3 => Nrv2dBuildingBlock.DecompressRaw(compressed, (int)h.UncompressedSize),
        5 => Nrv2dBuildingBlock.DecompressRawLe16(compressed, (int)h.UncompressedSize),
        7 => Nrv2dBuildingBlock.DecompressRawByte(compressed, (int)h.UncompressedSize),
        8 => Nrv2eBuildingBlock.DecompressRaw(compressed, (int)h.UncompressedSize),
        9 => Nrv2eBuildingBlock.DecompressRawLe16(compressed, (int)h.UncompressedSize),
        10 => Nrv2eBuildingBlock.DecompressRawByte(compressed, (int)h.UncompressedSize),
        // UPX stores a bare LZMA stream and carries the output size in its own
        // header. Our LzmaBuildingBlock expects the container we write ourselves —
        // five property bytes then a length — so handing it this payload makes it
        // read a length out of compressed data and try to allocate it.
        14 => throw new NotSupportedException(
          "UPX LZMA payloads are a bare stream sized by the PackHeader; decoding them needs a size-driven entry point that is not wired yet."),
        15 => TryDeflate(compressed, (int)h.UncompressedSize),
        _ => throw new NotSupportedException($"Unsupported UPX compression method {h.Method}."),
      };

      if (h.Layout == UpxReader.PackerHeaderLayout.Legacy && h.UncompressedAdler32 != 0) {
        var actualUncompressedAdler = Adler32.Compute(data);
        if (actualUncompressedAdler != h.UncompressedAdler32)
          return (null,
            $"UPX uncompressed Adler-32 mismatch: expected 0x{h.UncompressedAdler32:X8}, got 0x{actualUncompressedAdler:X8}.");
      }

      return (data, null);
    } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException
        or ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException) {
      return (null, $"UPX {UpxReader.MethodName(h.Method)} decompression failed: {ex.Message}");
    }
  }

  private static byte[] TryDeflate(byte[] compressed, int expectedSize) {
    var data = DeflateDecompressor.Decompress(compressed);
    if (data.Length != expectedSize)
      throw new InvalidDataException($"DEFLATE output length {data.Length} does not match UPX header length {expectedSize}.");
    return data;
  }

  private static (byte[] Payload, uint ExpectedSize)? TryLocateHeaderlessPePayload(UpxReader.Info info) {
    if (info.Kind != UpxReader.ContainerKind.Pe)
      return null;

    var target = info.PeSections.FirstOrDefault(s => s.Name == "UPX0" && s.VirtualSize > 0);
    var packed = info.PeSections.FirstOrDefault(s => s.Name == "UPX1" && s.RawSize > 0);
    if (target == null || packed == null)
      return null;
    if (packed.RawOffset >= info.Image.Length || packed.RawSize > info.Image.Length - packed.RawOffset)
      return null;

    var payload = info.Image.AsSpan((int)packed.RawOffset, (int)packed.RawSize).ToArray();
    return (payload, target.VirtualSize);
  }

  private static (byte[]? Data, string? Note) TryDecompressHeaderless(byte[] compressed, int expectedSize, UnpackOptions options) {
    if (expectedSize <= 0)
      return (null, "UPX headerless payload has no inferred decompressed size.");
    if (expectedSize > options.MaximumDecompressedSize)
      return (null, "UPX inferred decompressed size exceeds configured executable unpacking limit.");

    foreach (var method in new byte[] { 2, 3, 8, 4, 5, 9, 6, 7, 10, 14, 15 }) {
      try {
        var data = method switch {
          2 => Nrv2bBuildingBlock.DecompressRaw(compressed, expectedSize),
          4 => Nrv2bBuildingBlock.DecompressRawLe16(compressed, expectedSize),
          6 => Nrv2bBuildingBlock.DecompressRawByte(compressed, expectedSize),
          3 => Nrv2dBuildingBlock.DecompressRaw(compressed, expectedSize),
          5 => Nrv2dBuildingBlock.DecompressRawLe16(compressed, expectedSize),
          7 => Nrv2dBuildingBlock.DecompressRawByte(compressed, expectedSize),
          8 => Nrv2eBuildingBlock.DecompressRaw(compressed, expectedSize),
          9 => Nrv2eBuildingBlock.DecompressRawLe16(compressed, expectedSize),
          10 => Nrv2eBuildingBlock.DecompressRawByte(compressed, expectedSize),
          14 => null, // see the sized-payload path: a bare UPX LZMA stream has no container to read

          15 => TryDeflate(compressed, expectedSize),
          _ => throw new NotSupportedException(),
        };
        if (data is { } decoded && decoded.Length == expectedSize)
          return (decoded, UpxReader.MethodName(method));
      } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException
          or ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException) {
        // Try the next UPX codec variant; a headerless payload has no method byte.
      }
    }

    return (null, $"UPX headerless payload could not be decoded by managed NRV/LZMA/DEFLATE probes to the inferred size {expectedSize}.");
  }

  private static TransformResult ReverseFilter(byte[] decompressed, UpxReader.Info info) {
    if (info.Header is not { FilterId: > 0 } h)
      return new(decompressed, []);

    return new(decompressed, [
      new(ExecutableDiagnosticCode.TransformNotReversible,
        $"UPX filter {h.FilterId} is recorded in the PackHeader but no managed reversal is wired yet; filtered_payload.bin equals decompressed_payload.bin.")
    ]);
  }

  private static ExecutableUnpackCapabilities CapabilityForLevel(ExecutableUnpackLevel level, ExecutableImageInfo? imageInfo) {
    var caps = ExecutableUnpackCapabilities.CanDetect;
    if (level >= ExecutableUnpackLevel.PayloadLocated) caps |= ExecutableUnpackCapabilities.CanLocatePayload;
    if (level >= ExecutableUnpackLevel.PayloadDecompressed) caps |= ExecutableUnpackCapabilities.CanDecompressPayload;
    if (level >= ExecutableUnpackLevel.RuntimeMemoryImage) caps |= ExecutableUnpackCapabilities.CanBuildMemoryImage;
    if (level >= ExecutableUnpackLevel.RebuiltExecutable) caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
    caps |= imageInfo?.Container switch {
      ExecutableContainerKind.Pe => ExecutableUnpackCapabilities.SupportsPe,
      ExecutableContainerKind.Elf => ExecutableUnpackCapabilities.SupportsElf,
      ExecutableContainerKind.MachO or ExecutableContainerKind.FatMachO => ExecutableUnpackCapabilities.SupportsMachO,
      _ => ExecutableUnpackCapabilities.None,
    };
    caps |= imageInfo?.Architecture switch {
      CpuArchitecture.X86 => ExecutableUnpackCapabilities.SupportsX86,
      CpuArchitecture.X64 => ExecutableUnpackCapabilities.SupportsX64,
      CpuArchitecture.Arm32 => ExecutableUnpackCapabilities.SupportsArm32,
      CpuArchitecture.Arm64 => ExecutableUnpackCapabilities.SupportsArm64,
      _ => ExecutableUnpackCapabilities.None,
    };
    return caps;
  }

  private static void AddLegacySectionArtifacts(UpxReader.Info info, List<UnpackArtifact> artifacts) {
    var method = info.Header is { } h ? UpxReader.MethodName(h.Method).ToLowerInvariant() : "stored";
    foreach (var s in info.PeSections) {
      if (s.Name is not ("UPX0" or "UPX1" or "UPX2")) continue;
      var sectionMethod = s.Name == "UPX1" ? method : "stored";
      if (s.RawOffset == 0 || s.RawSize == 0 || s.RawOffset >= info.Image.Length) {
        artifacts.Add(new($"section_{s.Name}.bin", [], sectionMethod));
        continue;
      }
      var start = (int)s.RawOffset;
      var length = (int)Math.Min(s.RawSize, (uint)(info.Image.Length - start));
      artifacts.Add(new($"section_{s.Name}.bin", info.Image.AsSpan(start, length).ToArray(), sectionMethod));
    }
  }

  private static void AddRegionArtifacts(List<UnpackArtifact> artifacts, IReadOnlyList<ExecutableRegion> regions) {
    for (var i = 0; i < regions.Count; i++) {
      var bytes = regions[i].MemoryBytes;
      if (bytes == null) continue;
      var safe = Sanitize(regions[i].Name);
      artifacts.Add(new($"regions/region_{i:000}_{safe}.bin", bytes, "stored"));
    }
  }

  private static string Sanitize(string value) {
    var sb = new StringBuilder(value.Length);
    foreach (var c in value)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.Length == 0 ? "region" : sb.ToString();
  }

  private static byte[] BuildMetadataJson(UpxReader.Info info, ExecutableImageInfo? imageInfo) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"packer\": \"upx\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(imageInfo?.Container.ToString() ?? info.Kind.ToString()).ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(imageInfo?.Architecture.ToString() ?? "Unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"detectionConfidence\": \"{info.Confidence}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {info.Image.LongLength}");
    if (info.Header is { } h) {
      sb.Append(",\n");
      sb.Append(CultureInfo.InvariantCulture, $"  \"packerHeaderLayout\": \"{h.Layout}\",\n");
      sb.Append(CultureInfo.InvariantCulture, $"  \"format\": \"{UpxReader.FormatName(h.Format)}\",\n");
      sb.Append(CultureInfo.InvariantCulture, $"  \"method\": \"{UpxReader.MethodName(h.Method)}\",\n");
      sb.Append(CultureInfo.InvariantCulture, $"  \"compressedSize\": {h.CompressedSize},\n");
      sb.Append(CultureInfo.InvariantCulture, $"  \"uncompressedSize\": {h.UncompressedSize}\n");
    } else
      sb.Append('\n');
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildLegacyMetadata(UpxReader.Info info, string? decompressNote) {
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
    foreach (var s in info.PeSections)
      sb.Append(CultureInfo.InvariantCulture,
        $"section = {s.Name} vsize=0x{s.VirtualSize:X8} vaddr=0x{s.VirtualAddress:X8} rawSize=0x{s.RawSize:X8} rawOffset=0x{s.RawOffset:X8} flags=0x{s.Characteristics:X8}\n");

    if (info.Header is { } h) {
      sb.AppendLine();
      sb.AppendLine("[packer_header]");
      sb.Append(CultureInfo.InvariantCulture, $"offset = 0x{h.Offset:X}\n");
      sb.Append(CultureInfo.InvariantCulture, $"layout = {h.Layout}\n");
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

    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
