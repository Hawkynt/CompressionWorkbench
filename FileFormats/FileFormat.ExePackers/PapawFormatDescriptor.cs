#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Streams;
using Compression.Registry;
using FileFormat.Xz;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for Papaw-packed ELF executables. Papaw appends an
/// obfuscated XZ/LZMA2 payload plus a big-endian footer with original and
/// compressed lengths to an ELF decompressor stub; static unpacking restores the
/// XZ stream and emits the original executable bytes without executing the stub.
/// </summary>
public sealed class PapawFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private static readonly byte[] XzHeaderPrefix = [0xFD, 0x37, 0x7A, 0x58, 0x5A];

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Papaw";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Papaw executable wrapper";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  // A Papaw file is an ELF executable (content/footer-detected, no canonical
  // extension); ".elf" is the honest suggested-output extension, matching the
  // sibling ELF wrapper descriptors. Extensions stays empty to avoid collisions.
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".elf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("xz-lzma2", "XZ/LZMA2"), new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Papaw ELF executable wrapper - statically restores the appended XZ/LZMA2 payload and reconstructed original executable.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.CompressedSize,
        e.Method, false, false, null))
      .ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  internal static List<(string Name, byte[] Data, long CompressedSize, string Method)> BuildArtifacts(byte[] bytes) {
    var payload = LocatePayload(bytes);
    if (payload == null)
      throw new InvalidDataException("papaw: no valid appended XZ/LZMA2 payload footer was found.");

    var compressed = bytes[payload.PayloadOffset..(payload.PayloadOffset + payload.CompressedSize)];
    var restoredXz = RestoreXzEnvelope(compressed);
    var reconstructed = InflateXz(restoredXz);
    if (reconstructed.Length != payload.UncompressedSize)
      throw new InvalidDataException(
        $"papaw: decompressed size {reconstructed.Length} does not match footer size {payload.UncompressedSize}.");

    return [
      ("metadata.ini", BuildMetadata(bytes, payload, reconstructed.Length), compressed.Length, "stored"),
      ("diagnostics.json", BuildDiagnosticsJson(payload, reconstructed.Length), compressed.Length, "stored"),
      ("original_packed.bin", bytes, bytes.Length, "stored"),
      ("compressed_payload.papaw-xz", compressed, compressed.Length, "xz-lzma2-obfuscated"),
      ("compressed_payload.restored.xz", restoredXz, restoredXz.Length, "xz-lzma2"),
      ("reconstructed/original_executable.bin", reconstructed, compressed.Length, "stored"),
    ];
  }

  internal static PapawPayloadInfo? LocatePayload(byte[] bytes) {
    if (bytes.Length < 16 || bytes[0] != 0x7F || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F')
      return null;

    var footerOffset = bytes.Length - 8;
    var uncompressedSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(footerOffset));
    var compressedSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(footerOffset + 4));
    if (uncompressedSize == 0 || compressedSize < 12 || compressedSize > bytes.Length - 8)
      return null;

    var payloadOffset = bytes.Length - 8 - (int)compressedSize;
    if (payloadOffset <= 0)
      return null;

    var payload = bytes.AsSpan(payloadOffset, (int)compressedSize);
    if (payload[0] != 0 || payload[1] != 0 || payload[2] != 0 || payload[3] != 0x08 || payload[4] != 0)
      return null;

    try {
      var restored = RestoreXzEnvelope(payload.ToArray());
      var inflated = InflateXz(restored);
      if (inflated.Length != uncompressedSize)
        return null;
    } catch (InvalidDataException) {
      return null;
    } catch (EndOfStreamException) {
      return null;
    }

    return new(payloadOffset, (int)compressedSize, (int)uncompressedSize);
  }

  internal static byte[] RestoreXzEnvelope(byte[] obfuscated) {
    if (obfuscated.Length < 12)
      throw new InvalidDataException("papaw: obfuscated payload is too small to hold an XZ stream.");

    var restored = obfuscated.ToArray();
    XzHeaderPrefix.CopyTo(restored.AsSpan(0));
    restored[^2] = (byte)'Y';
    restored[^1] = (byte)'Z';
    return restored;
  }

  private static List<(string Name, byte[] Data, long CompressedSize, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return BuildArtifacts(ms.ToArray());
  }

  private static byte[] InflateXz(byte[] restoredXz) {
    using var input = new MemoryStream(restoredXz);
    using var xz = new XzStream(input, CompressionStreamMode.Decompress, leaveOpen: false);
    using var output = new MemoryStream();
    xz.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] BuildMetadata(byte[] image, PapawPayloadInfo payload, int reconstructedSize) {
    var sb = new StringBuilder();
    sb.AppendLine("[papaw]");
    sb.Append(CultureInfo.InvariantCulture, $"image_size = {image.Length}\n");
    sb.Append(CultureInfo.InvariantCulture, $"payload_offset = 0x{payload.PayloadOffset:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"compressed_size = {payload.CompressedSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"reconstructed_size = {reconstructedSize}\n");
    sb.AppendLine("capability_level = RebuiltExecutable");
    sb.AppendLine("note = Papaw appends an obfuscated XZ/LZMA2 payload to an ELF decompressor stub; no input code is executed.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildDiagnosticsJson(PapawPayloadInfo payload, int reconstructedSize) =>
    Encoding.UTF8.GetBytes(
      $$"""
      {
        "packer": "papaw",
        "container": "elf-wrapper",
        "capabilityLevel": "RebuiltExecutable",
        "canRebuildExecutable": true,
        "payloadOffset": {{payload.PayloadOffset}},
        "compressedSize": {{payload.CompressedSize}},
        "reconstructedSize": {{reconstructedSize}},
        "warnings": [
          "Papaw output keeps the decompressor stub as the outer ELF; reconstructed/original_executable.bin is the original input bytes."
        ],
        "outputs": [
          "compressed_payload.papaw-xz",
          "compressed_payload.restored.xz",
          "reconstructed/original_executable.bin",
          "metadata.ini",
          "diagnostics.json"
        ]
      }
      """);

  internal sealed record PapawPayloadInfo(int PayloadOffset, int CompressedSize, int UncompressedSize);
}
