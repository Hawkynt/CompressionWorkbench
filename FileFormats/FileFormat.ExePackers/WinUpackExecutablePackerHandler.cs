#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Represents a win upack executable packer handler.
/// </summary>
public sealed class WinUpackExecutablePackerHandler : IExecutablePackerHandler {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "winupack";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "WinUpack / Upack packed PE";

    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

    /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "WinUpack: not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSectionRanges(image);
    var hasVirtualUpack = sections.Any(s => s.RawSize == 0 && s.VirtualSize > 0 && IsUpackName(s.Name));
    var hasPayload = sections.Any(s => s.RawSize > 8 && s.RawOffset > 0);
    if ((hasVirtualUpack && hasPayload) || HasPsLayout(image))
      return new(true, this.Id, 0.9, []);

    return new(false, this.Id, 0, [
      new(ExecutableDiagnosticCode.PayloadNotFound, "WinUpack: no virtual .Upack target plus raw payload section was found.", true),
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
        ["packer"] = "WinUpack",
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

    var diagnostics = new List<ExecutableDiagnostic>();
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };

    var payload = LocatePayload(packed.OriginalImage, out var targetSize);
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;

    if (payload != null) {
      artifacts.Add(new("compressed_payload.bin", payload, "winupack"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
    } else
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "WinUpack payload section could not be located.", true));

    if (WinUpackLayoutReader.TryRead(packed.OriginalImage, options.MaximumDecompressedSize, out var layout)) {
      try {
        var unpacked = WinUpackStream.Decompress(packed.OriginalImage.AsSpan(layout.PayloadOffset), layout.ImageSize);
        WinUpackStream.UndoBranchFilter(unpacked, layout.ImageVirtualAddress, layout.FilterBase, layout.FilterCount, layout.FilterTag);
        artifacts.Add(new("decompressed_payload.bin", unpacked, "stored"));
        level = ExecutableUnpackLevel.PayloadDecompressed;
        caps |= ExecutableUnpackCapabilities.CanLocatePayload | ExecutableUnpackCapabilities.CanDecompressPayload;
        diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
          $"WinUpack payload decompressed to {unpacked.Length} bytes of mapped image at 0x{layout.ImageVirtualAddress:X8}. " +
          "The import directory and base relocations are rebuilt by the stub at run time and are therefore not part of this image."));
      } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) {
        diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed, $"WinUpack payload failed to decompress: {ex.Message}", true));
      }
    } else
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        $"WinUpack parameter block was not recognised, so the payload could not be decompressed. Virtual target size: {targetSize} bytes.",
        true));

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static byte[]? LocatePayload(byte[] image, out uint targetSize) {
    targetSize = 0;
    var sections = PackerScanner.GetPeSectionRanges(image);
    var target = sections.FirstOrDefault(s => s.RawSize == 0 && s.VirtualSize > 0 && IsUpackName(s.Name));
    if (target.VirtualSize == 0 && HasPsLayout(image))
      target = sections.FirstOrDefault();
    if (target.VirtualSize == 0)
      return null;
    targetSize = target.VirtualSize;

    var payload = sections
      .Where(s => s.RawSize > 8 && s.RawOffset > 0 && s.RawOffset < image.Length)
      .OrderByDescending(s => s.RawSize)
      .FirstOrDefault();
    if (payload.RawSize == 0 || payload.RawOffset >= image.Length)
      return null;

    var length = (int)Math.Min(payload.RawSize, (uint)(image.Length - payload.RawOffset));
    return image.AsSpan((int)payload.RawOffset, length).ToArray();
  }

  private static bool IsUpackName(string name) =>
    name.Contains("upack", StringComparison.OrdinalIgnoreCase);

  private static bool HasPsLayout(ReadOnlySpan<byte> image) {
    var sections = ReadPeSectionLayouts(image);
    if (sections.Count != 3 || !sections[0].Name.StartsWith("PS", StringComparison.Ordinal))
      return false;

    var entryPoint = ReadPeEntryPoint(image);
    if (entryPoint == null)
      return false;

    var first = sections[0];
    var firstEnd = first.VirtualAddress + Math.Max(first.VirtualSize, first.RawSize);
    return entryPoint.Value >= first.VirtualAddress &&
      entryPoint.Value < firstEnd &&
      sections[1].RawSize > 8 &&
      sections[1].RawOffset > 0;
  }

  private readonly record struct PeSectionLayout(
    string Name,
    uint VirtualAddress,
    uint VirtualSize,
    uint RawOffset,
    uint RawSize);

  private static IReadOnlyList<PeSectionLayout> ReadPeSectionLayouts(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return [];

    var peOffset = BinaryPrimitives.ReadInt32LittleEndian(image[0x3c..]);
    if (peOffset < 0 || peOffset + 24 > image.Length)
      return [];

    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 6)..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 20)..]);
    var sectionOffset = peOffset + 24 + optionalSize;
    if (sectionOffset < 0 || sectionOffset + sectionCount * 40 > image.Length)
      return [];

    var sections = new List<PeSectionLayout>(sectionCount);
    for (var i = 0; i < sectionCount; i++) {
      var offset = sectionOffset + i * 40;
      var nameSpan = image.Slice(offset, 8);
      var terminator = nameSpan.IndexOf((byte)0);
      if (terminator < 0)
        terminator = 8;
      var name = Encoding.ASCII.GetString(nameSpan[..terminator]);
      sections.Add(new(
        name,
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 12)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 8)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 20)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 16)..])));
    }
    return sections;
  }

  private static uint? ReadPeEntryPoint(ReadOnlySpan<byte> image) {
    if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
      return null;
    var peOffset = BinaryPrimitives.ReadInt32LittleEndian(image[0x3c..]);
    if (peOffset < 0 || peOffset + 44 > image.Length)
      return null;
    if (image[peOffset] != (byte)'P' || image[peOffset + 1] != (byte)'E' || image[peOffset + 2] != 0 || image[peOffset + 3] != 0)
      return null;
    var optionalOffset = peOffset + 24;
    return BinaryPrimitives.ReadUInt32LittleEndian(image[(optionalOffset + 16)..]);
  }

  private static byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"winupack\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"compressionCore\": \"upack-range-coder\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
