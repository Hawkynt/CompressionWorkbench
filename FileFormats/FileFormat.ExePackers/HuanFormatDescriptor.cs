#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Crypto;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Static unpacker for Huan PE64 loader outputs. Huan embeds the original PE
/// in a .huan section as: original length, encrypted length, AES-128 key,
/// AES-CBC IV, encrypted bytes padded with zeroes to a 16-byte boundary.
/// </summary>
public sealed class HuanFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Huan";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Huan PE64 encrypted loader";
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
public string DefaultExtension => ".exe";
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
public IReadOnlyList<FormatMethodInfo> Methods => [new("aes-128-cbc", "AES-128 CBC"), new("stored", "Stored")];
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
    "Huan PE64 encrypted loader - statically decrypts the .huan AES-128-CBC payload and reconstructs the embedded PE.";

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
      throw new InvalidDataException("huan: .huan AES payload section was not found.");

    var encrypted = bytes.AsSpan(payload.EncryptedOffset, payload.EncryptedLength).ToArray();
    var decryptedPadded = AesCryptor.DecryptCbcNoPaddingAny(encrypted, payload.Key, payload.Iv);
    if (payload.OriginalLength > decryptedPadded.Length)
      throw new InvalidDataException("huan: original length exceeds decrypted payload length.");
    var reconstructed = decryptedPadded.AsSpan(0, payload.OriginalLength).ToArray();
    if (reconstructed.Length < 2 || reconstructed[0] != 'M' || reconstructed[1] != 'Z')
      throw new InvalidDataException("huan: decrypted payload is not a PE/MZ image.");

    return [
      ("metadata.ini", BuildMetadata(bytes, payload, reconstructed.Length), encrypted.Length, "stored"),
      ("diagnostics.json", BuildDiagnosticsJson(payload, reconstructed.Length), encrypted.Length, "stored"),
      ("original_packed.bin", bytes, bytes.Length, "stored"),
      ("encrypted_payload.bin", encrypted, encrypted.Length, "aes-128-cbc"),
      ("decrypted_payload_padded.bin", decryptedPadded, encrypted.Length, "stored"),
      ("reconstructed/reconstructed.exe", reconstructed, encrypted.Length, "stored"),
    ];
  }

  internal static HuanPayloadInfo? LocatePayload(byte[] bytes) {
    if (!TryFindPeSection(bytes, ".huan", out var offset, out var size))
      return null;
    if (size < 40 || offset < 0 || offset + size > bytes.Length)
      return null;

    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    var encryptedLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
    if (originalLength <= 0 || encryptedLength <= 0 || encryptedLength % 16 != 0)
      return null;
    if (40 + encryptedLength > size || offset + 40 + encryptedLength > bytes.Length)
      return null;

    return new(
      offset,
      size,
      originalLength,
      encryptedLength,
      bytes.AsSpan(offset + 8, 16).ToArray(),
      bytes.AsSpan(offset + 24, 16).ToArray(),
      offset + 40
    );
  }

  private static List<(string Name, byte[] Data, long CompressedSize, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return BuildArtifacts(ms.ToArray());
  }

  private static bool TryFindPeSection(byte[] bytes, string name, out int rawOffset, out int rawSize) {
    rawOffset = 0;
    rawSize = 0;
    if (bytes.Length < 0x100 || bytes[0] != 'M' || bytes[1] != 'Z')
      return false;
    var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
    if (peOffset < 0 || peOffset + 24 > bytes.Length || !bytes.AsSpan(peOffset, 4).SequenceEqual("PE\0\0"u8))
      return false;
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 6));
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 20));
    var sectionOffset = peOffset + 24 + optionalSize;
    if (sectionOffset < 0 || sectionOffset + sectionCount * 40 > bytes.Length)
      return false;

    for (var i = 0; i < sectionCount; i++) {
      var s = sectionOffset + i * 40;
      var sectionName = Encoding.ASCII.GetString(bytes, s, 8).TrimEnd('\0');
      if (!string.Equals(sectionName, name, StringComparison.Ordinal))
        continue;
      rawSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 16)));
      rawOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 20)));
      return rawOffset >= 0 && rawSize >= 0 && rawOffset + rawSize <= bytes.Length;
    }

    return false;
  }

  private static byte[] BuildMetadata(byte[] image, HuanPayloadInfo payload, int reconstructedSize) {
    var sb = new StringBuilder();
    sb.AppendLine("[huan]");
    sb.Append(CultureInfo.InvariantCulture, $"image_size = {image.Length}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_offset = 0x{payload.SectionOffset:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_size = {payload.SectionSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"original_length = {payload.OriginalLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"encrypted_length = {payload.EncryptedLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"reconstructed_size = {reconstructedSize}\n");
    sb.AppendLine("cipher = aes-128-cbc");
    sb.AppendLine("padding = zeroes");
    sb.AppendLine("capability_level = RebuiltExecutable");
    sb.AppendLine("note = Huan .huan payload is decrypted statically; no loader code is executed.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildDiagnosticsJson(HuanPayloadInfo payload, int reconstructedSize) =>
    Encoding.UTF8.GetBytes(
      $$"""
      {
        "packer": "huan",
        "container": "pe64-loader",
        "capabilityLevel": "RebuiltExecutable",
        "canRebuildExecutable": true,
        "cipher": "aes-128-cbc",
        "sectionOffset": {{payload.SectionOffset}},
        "originalLength": {{payload.OriginalLength}},
        "encryptedLength": {{payload.EncryptedLength}},
        "reconstructedSize": {{reconstructedSize}},
        "warnings": [
          "Huan embeds a complete encrypted PE payload; reconstructed/reconstructed.exe is the embedded original PE, not a regenerated loader."
        ],
        "outputs": [
          "encrypted_payload.bin",
          "decrypted_payload_padded.bin",
          "reconstructed/reconstructed.exe",
          "metadata.ini",
          "diagnostics.json"
        ]
      }
      """);

  internal sealed record HuanPayloadInfo(
    int SectionOffset,
    int SectionSize,
    int OriginalLength,
    int EncryptedLength,
    byte[] Key,
    byte[] Iv,
    int EncryptedOffset
  );
}
