#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Streams;
using Compression.Registry;
using FileFormat.Zstd;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for GoPacker executables. GoPacker appends a
/// Zstandard-compressed copy of the original executable, followed by an
/// 8-byte little-endian compressed length and the ASCII footer "LALALALA".
/// </summary>
public sealed class GoPackerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private static ReadOnlySpan<byte> FooterMagic => "LALALALA"u8;
  private static ReadOnlySpan<byte> ZstdFrameMagic => [0x28, 0xB5, 0x2F, 0xFD];

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "GoPacker";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "GoPacker executable wrapper";
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
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".bin";
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
public IReadOnlyList<FormatMethodInfo> Methods => [new("zstd", "Zstandard"), new("stored", "Stored")];
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
    "GoPacker executable wrapper - statically extracts the appended Zstandard payload and reconstructed original executable.";

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
      throw new InvalidDataException("gopacker: no valid appended Zstandard payload footer was found.");

    var compressed = bytes[payload.PayloadOffset..(payload.PayloadOffset + payload.CompressedSize)];
    var reconstructed = InflateZstd(compressed);
    return [
      ("metadata.ini", BuildMetadata(bytes, payload, reconstructed.Length), compressed.Length, "stored"),
      ("diagnostics.json", BuildDiagnosticsJson(payload, reconstructed.Length), compressed.Length, "stored"),
      ("original_packed.bin", bytes, bytes.Length, "stored"),
      ("compressed_payload.zst", compressed, compressed.Length, "zstd"),
      ("reconstructed/original_executable.bin", reconstructed, compressed.Length, "stored"),
    ];
  }

  internal static GoPackerPayloadInfo? LocatePayload(byte[] bytes) {
    var trailerSize = 8 + FooterMagic.Length;
    if (bytes.Length < trailerSize + 4)
      return null;

    if (!bytes.AsSpan(bytes.Length - FooterMagic.Length).SequenceEqual(FooterMagic))
      return null;

    var lengthOffset = bytes.Length - trailerSize;
    var compressedSize64 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(lengthOffset));
    if (compressedSize64 == 0 || compressedSize64 > (ulong)lengthOffset)
      return null;

    var compressedSize = checked((int)compressedSize64);
    var payloadOffset = lengthOffset - compressedSize;
    if (payloadOffset < 0 || payloadOffset + 4 > bytes.Length)
      return null;

    if (!bytes.AsSpan(payloadOffset, 4).SequenceEqual(ZstdFrameMagic))
      return null;

    return new(payloadOffset, compressedSize);
  }

  private static List<(string Name, byte[] Data, long CompressedSize, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return BuildArtifacts(ms.ToArray());
  }

  private static byte[] InflateZstd(byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var zstd = new ZstdStream(input, CompressionStreamMode.Decompress, leaveOpen: false);
    using var output = new MemoryStream();
    zstd.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] BuildMetadata(byte[] image, GoPackerPayloadInfo payload, int reconstructedSize) {
    var sb = new StringBuilder();
    sb.AppendLine("[gopacker]");
    sb.Append(CultureInfo.InvariantCulture, $"image_size = {image.Length}\n");
    sb.Append(CultureInfo.InvariantCulture, $"payload_offset = 0x{payload.PayloadOffset:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"compressed_size = {payload.CompressedSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"reconstructed_size = {reconstructedSize}\n");
    sb.AppendLine("capability_level = RebuiltExecutable");
    sb.AppendLine("note = GoPacker appends a Zstandard-compressed executable to its stub; no input code is executed.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildDiagnosticsJson(GoPackerPayloadInfo payload, int reconstructedSize) =>
    Encoding.UTF8.GetBytes(
      $$"""
      {
        "packer": "gopacker",
        "container": "stub-plus-zstd-payload",
        "capabilityLevel": "RebuiltExecutable",
        "canRebuildExecutable": true,
        "payloadOffset": {{payload.PayloadOffset}},
        "compressedSize": {{payload.CompressedSize}},
        "reconstructedSize": {{reconstructedSize}},
        "warnings": [
          "GoPacker output keeps the runtime stub as the outer executable; reconstructed/original_executable.bin is the original input bytes."
        ],
        "outputs": [
          "compressed_payload.zst",
          "reconstructed/original_executable.bin",
          "metadata.ini",
          "diagnostics.json"
        ]
      }
      """);

  internal sealed record GoPackerPayloadInfo(int PayloadOffset, int CompressedSize);
}
