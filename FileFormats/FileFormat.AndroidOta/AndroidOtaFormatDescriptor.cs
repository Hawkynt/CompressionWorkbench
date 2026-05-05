#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AndroidOta;

/// <summary>
/// Android A/B OTA payload (<c>payload.bin</c>) — the Chromium Autoupdate (<c>CrAU</c>) container
/// used by Android over-the-air updates. The payload embeds a protobuf <c>DeltaArchiveManifest</c>
/// plus a metadata signature and data blobs.
///
/// <para>Protobuf parsing is intentionally out of scope: this descriptor surfaces the structural
/// regions (manifest bytes, signature bytes, data blob) as raw entries so callers can drive their
/// own parsers downstream.</para>
/// </summary>
public sealed class AndroidOtaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "AndroidOta";
  public string DisplayName => "Android OTA payload";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  // Android OTA payload files are literally named "payload.bin"; the generic ".bin"
  // extension belongs to BIN/CUE disc images. Rely on the "CrAU" magic for detection.
  public string DefaultExtension => ".bin";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("CrAU"u8.ToArray(), Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Android A/B OTA payload (Chromium Autoupdate 'CrAU' container).";

  private sealed record OtaLayout(
    long FullSize,
    ulong Version,
    ulong ManifestSize,
    uint MetadataSignatureSize,
    long ManifestOffset,
    long MetadataSignatureOffset,
    long DataOffset,
    long DataSize
  );

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var layout = ReadLayout(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.bin", layout.FullSize, layout.FullSize, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "Stored", false, false, null),
      new ArchiveEntryInfo(2, "manifest.pb", (long)layout.ManifestSize, (long)layout.ManifestSize, "Stored", false, false, null),
      new ArchiveEntryInfo(3, "metadata_signature.bin", layout.MetadataSignatureSize, layout.MetadataSignatureSize, "Stored", false, false, null),
      new ArchiveEntryInfo(4, "data.bin", layout.DataSize, layout.DataSize, "Stored", false, false, null),
    ];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var layout = ReadLayout(stream);

    if (Wants(files, "FULL.bin"))
      WriteFile(outputDir, "FULL.bin", ReadRange(stream, 0, layout.FullSize));

    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(layout)));

    if (Wants(files, "manifest.pb"))
      WriteFile(outputDir, "manifest.pb", ReadRange(stream, layout.ManifestOffset, (long)layout.ManifestSize));

    if (Wants(files, "metadata_signature.bin"))
      WriteFile(outputDir, "metadata_signature.bin", ReadRange(stream, layout.MetadataSignatureOffset, layout.MetadataSignatureSize));

    if (Wants(files, "data.bin"))
      WriteFile(outputDir, "data.bin", ReadRange(stream, layout.DataOffset, layout.DataSize));
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static OtaLayout ReadLayout(Stream stream) {
    if (!stream.CanSeek)
      throw new InvalidDataException("Android OTA descriptor requires a seekable stream.");
    stream.Position = 0;
    var fullSize = stream.Length;

    Span<byte> header = stackalloc byte[24];
    ReadExact(stream, header);
    if (header[0] != (byte)'C' || header[1] != (byte)'r' || header[2] != (byte)'A' || header[3] != (byte)'U')
      throw new InvalidDataException("Not an Android OTA payload (missing CrAU magic).");

    var version = BinaryPrimitives.ReadUInt64BigEndian(header[4..12]);
    var manifestSize = BinaryPrimitives.ReadUInt64BigEndian(header[12..20]);
    var metadataSignatureSize = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);

    const long headerBytes = 24;
    var manifestOffset = headerBytes;
    var metadataSignatureOffset = (long)((ulong)manifestOffset + manifestSize);
    var dataOffset = metadataSignatureOffset + metadataSignatureSize;
    var dataSize = Math.Max(0, fullSize - dataOffset);

    if ((long)manifestSize < 0 || (ulong)manifestOffset + manifestSize > (ulong)fullSize)
      throw new InvalidDataException("Android OTA manifest_size exceeds stream length.");
    if (dataOffset > fullSize)
      throw new InvalidDataException("Android OTA metadata region exceeds stream length.");

    return new OtaLayout(
      FullSize: fullSize,
      Version: version,
      ManifestSize: manifestSize,
      MetadataSignatureSize: metadataSignatureSize,
      ManifestOffset: manifestOffset,
      MetadataSignatureOffset: metadataSignatureOffset,
      DataOffset: dataOffset,
      DataSize: dataSize);
  }

  private static string BuildMetadataIni(OtaLayout layout) {
    var sb = new StringBuilder();
    sb.Append("[AndroidOta]\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={layout.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"manifest_size={layout.ManifestSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"metadata_signature_size={layout.MetadataSignatureSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"manifest_offset={layout.ManifestOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"metadata_signature_offset={layout.MetadataSignatureOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"data_start_offset={layout.DataOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"data_size={layout.DataSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_size={layout.FullSize}\n");
    return sb.ToString();
  }

  private static byte[] ReadRange(Stream stream, long offset, long size) {
    if (size <= 0) return [];
    if (size > int.MaxValue)
      throw new InvalidDataException("Android OTA region too large to extract in one allocation.");
    stream.Position = offset;
    var buf = new byte[(int)size];
    ReadExact(stream, buf);
    return buf;
  }

  private static void ReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n <= 0) throw new EndOfStreamException("Unexpected end of Android OTA stream.");
      read += n;
    }
  }
}
