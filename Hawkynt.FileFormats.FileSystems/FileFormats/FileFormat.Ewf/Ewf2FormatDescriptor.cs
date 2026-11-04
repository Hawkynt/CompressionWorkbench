using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ewf;

/// <summary>
/// EnCase 7 Evidence File Format version 2 (EWF2-Ex01): the forensic image's media as
/// <c>media.raw</c> plus its device information and case data as <c>metadata.ini</c>. Segment sets
/// (.Ex02 onward) are followed when the image is opened from a file; extraction checks the stored
/// MD5 and SHA-1. Create writes a single-segment Ex01.
/// </summary>
public sealed class Ewf2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {
  /// <inheritdoc />
  public string Id => "Ewf2";
  /// <inheritdoc />
  public string DisplayName => "EnCase EWF2 (Ex01)";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <inheritdoc />
  public string DefaultExtension => ".ex01";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".ex01"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x45, 0x56, 0x46, 0x32, 0x0D, 0x0A, 0x81, 0x00], Confidence: 0.95)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zlib", "Zlib-compressed chunks"), new("stored", "Stored chunks")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "EnCase 7 Ex01 forensic media image: zlib, bzip2, stored and pattern-filled chunks across segment sets, " +
    "checked against the stored MD5 and SHA-1. Encrypted and Lx01 (logical) sets are refused.";

  /// <inheritdoc />
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    reason = input.IsDirectory ? AcceptedInputsDescription : null;
    return !input.IsDirectory;
  }

  /// <inheritdoc />
  public long? MaxTotalArchiveSize => null;
  /// <inheritdoc />
  public string AcceptedInputsDescription => "One raw media image, exposed as media.raw; source filenames and file trees are not stored.";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var image = Ewf2Image.Open(stream);
    var metadata = Metadata(image);
    return [
      new ArchiveEntryInfo(0, "media.raw", image.MediaSize, -1, image.CompressionMethod == Ewf2Image.CompressionNone ? "stored" : "zlib", false, false, null, "media"),
      new ArchiveEntryInfo(1, "metadata.ini", metadata.Length, metadata.Length, "generated", false, false, null, "metadata"),
    ];
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var image = Ewf2Image.Open(stream);
    using var media = new Ewf2MediaStream(image);
    if (files is not { Length: > 0 } || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", Metadata(image));
    if (files is { Length: > 0 } && !MatchesFilter("media.raw", files))
      return;
    var path = Path.Combine(outputDir, "media.raw");
    try {
      using (var target = CreateEntryFile(outputDir, "media.raw"))
        CopyVerified(image, media, target);
    } catch (InvalidDataException) {
      File.Delete(path);
      throw;
    }
  }

  /// <inheritdoc />
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    var image = Ewf2Image.Open(archive);
    using var media = new Ewf2MediaStream(image);
    if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase))
      return Metadata(image);
    if (!string.Equals(entryName, "media.raw", StringComparison.OrdinalIgnoreCase))
      return [];
    using var buffer = new MemoryStream();
    CopyVerified(image, media, buffer);
    return buffer.ToArray();
  }

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    if (!string.IsNullOrEmpty(options?.Password))
      throw new NotSupportedException("EWF2: EnCase's Ex01 encryption is not published and is not written.");
    if (inputs.Count > 1 || inputs.Any(i => i.IsDirectory))
      throw new ArgumentException(AcceptedInputsDescription, nameof(inputs));
    var media = inputs.Count == 0 ? [] : inputs[0].ReadContent();
    var stored = string.Equals(options?.MethodName, "stored", StringComparison.OrdinalIgnoreCase);
    new Ewf2Writer { Compress = !stored }.Write(output, media);
  }

  /// <summary>Copies the media while hashing it; a mismatch with the stored MD5 or SHA-1 is an error.</summary>
  private static void CopyVerified(Ewf2Image image, Stream media, Stream target) {
    using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    var buffer = new byte[1 << 16];
    int read;
    while ((read = media.Read(buffer)) > 0) {
      md5.AppendData(buffer, 0, read);
      sha1.AppendData(buffer, 0, read);
      target.Write(buffer, 0, read);
    }
    if (image.Md5 != null && !md5.GetHashAndReset().AsSpan().SequenceEqual(image.Md5))
      throw new InvalidDataException("EWF2: the media does not match its stored MD5.");
    if (image.Sha1 != null && !sha1.GetHashAndReset().AsSpan().SequenceEqual(image.Sha1))
      throw new InvalidDataException("EWF2: the media does not match its stored SHA-1.");
  }

  private static byte[] Metadata(Ewf2Image image) {
    var sb = new StringBuilder();
    sb.AppendLine("[ewf2]");
    sb.Append(CultureInfo.InvariantCulture, $"set_identifier = {image.SetIdentifier}\n");
    sb.Append(CultureInfo.InvariantCulture, $"segments = {image.SegmentCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"compression = {image.CompressionMethod}\n");
    sb.Append(CultureInfo.InvariantCulture, $"media_size = {image.MediaSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bytes_per_sector = {image.BytesPerSector}\n");
    sb.Append(CultureInfo.InvariantCulture, $"sectors_per_chunk = {image.SectorsPerChunk}\n");
    if (image.Md5 != null)
      sb.Append(CultureInfo.InvariantCulture, $"md5 = {Convert.ToHexStringLower(image.Md5)}\n");
    if (image.Sha1 != null)
      sb.Append(CultureInfo.InvariantCulture, $"sha1 = {Convert.ToHexStringLower(image.Sha1)}\n");
    sb.AppendLine("[device]");
    foreach (var (tag, value) in image.DeviceInformation)
      sb.Append(CultureInfo.InvariantCulture, $"{tag} = {value}\n");
    sb.AppendLine("[case]");
    foreach (var (tag, value) in image.CaseData)
      sb.Append(CultureInfo.InvariantCulture, $"{tag} = {value}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
