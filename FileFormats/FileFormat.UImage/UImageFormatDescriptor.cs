#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.UImage;

/// <summary>
/// Pseudo-archive descriptor for U-Boot legacy uImage containers (<c>mkimage</c>
/// output). Exposes <c>metadata.ini</c>, <c>header.bin</c> (the 64-byte legacy
/// header) and <c>payload.bin</c> (the compressed body verbatim). When the body
/// compression is <c>none</c> an additional <c>payload_decompressed.bin</c> alias is
/// emitted. The one native payload is mutable; the other entries are renderings of
/// the fixed header or aliases of that payload.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.u-boot.org/</c> — U-Boot documentation</description></item>
///   <item><description><c>https://github.com/u-boot/u-boot</c> — U-Boot sources — <c>include/image.h</c> defines the 64-byte legacy header</description></item>
/// </list>
/// </summary>
public sealed class UImageFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveShrinkable, IArchiveLayoutMap,
    ISyntheticEntryNames {

  private static readonly IReadOnlySet<string> SyntheticNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    UImageWriter.MetadataName,
    UImageWriter.HeaderName,
    UImageWriter.DecompressedName,
  };

  /// <inheritdoc />
  public IReadOnlySet<string> SyntheticEntryNames => SyntheticNames;

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "UImage";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "U-Boot uImage";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".uimg";
  /// <remarks>
  /// Extensions are intentionally empty — <c>.img</c>/<c>.bin</c>/<c>.uimg</c>
  /// are all overloaded by other firmware formats, so we rely on the distinctive
  /// <c>0x27051956</c> magic at offset 0 for detection.
  /// </remarks>
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
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x27, 0x05, 0x19, 0x56], Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
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
    "U-Boot legacy uImage — 64-byte BE header + compressed body (kernel/ramdisk/fdt).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.LongLength, CompressedSize: e.Data.LongLength,
      Method: e.Method, IsDirectory: false, IsEncrypted: false, LastModified: null)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Writes a fresh uImage: the single payload input becomes the body, a
  /// <c>metadata.ini</c> alongside it -- the one this descriptor's own reader
  /// renders -- supplies the header fields, and both CRCs are computed the way
  /// <c>mkimage</c> computes them.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var (header, body) = UImageWriter.From(inputs);
    UImageWriter.Write(output, header, body);
  }

  /// <summary>
  /// Replaces the one native payload and/or edits its header renderings. Legacy
  /// uImage is a single-payload container, so adding an arbitrary non-synthetic
  /// input means replacing that payload rather than manufacturing a second member.
  /// Dead trailing bytes are preserved verbatim after the new declared payload.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    RequireEditable(archive);

    var source = ReadAll(archive);
    var image = ReadValidImage(source);
    var payloadEnd = checked(UImageReader.HeaderSize + (int)image.DataSize);
    var trailer = source[payloadEnd..];

    byte[]? rawHeader = null;
    byte[]? metadata = null;
    byte[]? storedPayload = null;
    byte[]? decompressedPayload = null;
    byte[]? arbitraryPayload = null;

    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      var leaf = Path.GetFileName(input.ArchiveName);
      if (leaf.Equals(UImageWriter.MetadataName, StringComparison.OrdinalIgnoreCase)) {
        metadata = input.ReadContent();
        continue;
      }
      if (leaf.Equals(UImageWriter.HeaderName, StringComparison.OrdinalIgnoreCase)) {
        rawHeader = input.ReadContent();
        continue;
      }
      if (leaf.Equals(UImageWriter.PayloadName, StringComparison.OrdinalIgnoreCase)) {
        storedPayload = input.ReadContent();
        continue;
      }
      if (leaf.Equals(UImageWriter.DecompressedName, StringComparison.OrdinalIgnoreCase)) {
        decompressedPayload = input.ReadContent();
        continue;
      }
      if (arbitraryPayload is not null)
        throw new InvalidOperationException("Legacy uImage can contain only one payload.");
      arbitraryPayload = input.ReadContent();
    }

    if (arbitraryPayload is not null && (storedPayload is not null || decompressedPayload is not null))
      throw new InvalidOperationException("Legacy uImage can contain only one payload.");

    var header = UImageWriter.From(image);
    if (rawHeader is not null)
      header = UImageWriter.FromHeaderBytes(rawHeader);
    if (metadata is not null)
      header = UImageWriter.ApplyMetadata(header, metadata);

    var body = image.Body;
    if (storedPayload is not null)
      body = storedPayload;
    else if (decompressedPayload is not null) {
      body = decompressedPayload;
      header = header with { Compression = 0 };
    } else if (arbitraryPayload is not null) {
      body = arbitraryPayload;
      header = header with { Compression = 0 };
    }

    WholeImageRebuildCommitter.Replace(archive,
      candidate => {
        UImageWriter.Write(candidate, header, body);
        candidate.Write(trailer);
      },
      ValidateCandidate);
  }

  /// <summary>
  /// Removes the sole live payload. The header/metadata entries are synthetic and
  /// cannot be removed independently; removing the decompressed alias is treated
  /// as removing the payload it represents.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    entryNames ??= [];
    if (entryNames.Any(IsPayloadName))
      this.Purge(archive);
  }

  /// <summary>
  /// Removes the live payload while preserving outer size. The former body becomes
  /// dead trailer space, intentionally left intact; <see cref="IWipeEmpty"/> can
  /// subsequently overwrite it when forensic erasure is requested.
  /// </summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    RequireEditable(archive);

    var source = ReadAll(archive);
    var image = ReadValidImage(source);
    var header = UImageWriter.From(image);
    var tail = source[UImageReader.HeaderSize..];

    WholeImageRebuildCommitter.Replace(archive,
      candidate => {
        UImageWriter.Write(candidate, header, []);
        candidate.Write(tail);
      },
      ValidateCandidate);
  }

  /// <summary>
  /// Removes bytes after the declared payload. Such a trailer is outside the
  /// legacy uImage described by <c>ih_size</c>; the header and live payload remain
  /// byte-identical. Invalid images are copied through unchanged.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("uImage shrink requires a readable, seekable input.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("uImage shrink requires a writable, seekable output.", nameof(output));

    long targetLength;
    try {
      var header = ReadValidatedHeader(input, validateDataCrc: true);
      targetLength = UImageReader.HeaderSize + (long)header.DataSize;
    } catch (InvalidDataException) {
      CopyWhole(input, output);
      return;
    } catch (EndOfStreamException) {
      CopyWhole(input, output);
      return;
    }

    if (ReferenceEquals(input, output)) {
      if (input.Length > targetLength)
        input.SetLength(targetLength);
      input.Position = Math.Min(input.Position, input.Length);
      return;
    }

    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    CopyExactly(input, output, targetLength);
  }

  /// <summary>
  /// Enumerates the fixed header, declared payload and any dead trailer bytes.
  /// The map is emitted only for a structurally valid image whose two CRCs match,
  /// so generic wipe never guesses about malformed input.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanSeek)
      return [];

    try {
      var header = ReadValidatedHeader(archive, validateDataCrc: true);
      var payloadEnd = UImageReader.HeaderSize + (long)header.DataSize;
      var result = new List<DefragBlockInfo>(3) {
        new(0, UImageReader.HeaderSize, DefragBlockKind.MetadataReserved, UImageWriter.HeaderName),
      };
      if (header.DataSize > 0)
        result.Add(new(UImageReader.HeaderSize, header.DataSize, DefragBlockKind.Used, UImageWriter.PayloadName));
      if (archive.Length > payloadEnd)
        result.Add(new(payloadEnd, archive.Length - payloadEnd, DefragBlockKind.Free, "trailing bytes"));
      return result;
    } catch (InvalidDataException) {
      return [];
    } catch (EndOfStreamException) {
      return [];
    }
  }

  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    var img = UImageReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    var entries = new List<(string, byte[], string)> {
      (UImageWriter.MetadataName, BuildMetadata(img), "stored"),
      (UImageWriter.HeaderName, img.Header, "stored"),
    };
    if (img.Body.Length > 0) entries.Add((UImageWriter.PayloadName, img.Body, "stored"));

    // Only the identity ('none') scheme is handled inline to keep this project
    // dependency-light. For the real compression schemes the caller should feed
    // payload.bin through FileFormat.Gzip/Bzip2/Lzma/Lzop/Lz4/Zstd.
    if (img.Compression == 0 && img.Body.Length > 0)
      entries.Add((UImageWriter.DecompressedName, img.Body, "stored"));

    return entries;
  }

  private static byte[] BuildMetadata(UImageReader.UImage i) {
    var sb = new StringBuilder();
    sb.AppendLine("[uimage]");
    sb.Append(CultureInfo.InvariantCulture, $"magic = 0x{i.Magic:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"name = {i.Name}\n");
    sb.Append(CultureInfo.InvariantCulture, $"timestamp = {i.Timestamp}\n");
    sb.Append(CultureInfo.InvariantCulture, $"data_size = {i.DataSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"load_address = 0x{i.LoadAddress:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"entry_point = 0x{i.EntryPoint:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"os = {i.Os} ({UImageReader.OsName(i.Os)})\n");
    sb.Append(CultureInfo.InvariantCulture, $"arch = {i.Architecture} ({UImageReader.ArchName(i.Architecture)})\n");
    sb.Append(CultureInfo.InvariantCulture, $"type = {i.Type} ({UImageReader.TypeName(i.Type)})\n");
    sb.Append(CultureInfo.InvariantCulture, $"comp = {i.Compression} ({UImageReader.CompressionName(i.Compression)})\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"header_crc_stored = 0x{i.HeaderCrc:X8}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"header_crc_computed = 0x{i.ComputedHeaderCrc:X8}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"header_crc_ok = {(i.HeaderCrc == i.ComputedHeaderCrc).ToString().ToLowerInvariant()}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"data_crc_stored = 0x{i.DataCrc:X8}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"data_crc_computed = 0x{i.ComputedDataCrc:X8}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"data_crc_ok = {(i.DataCrc == i.ComputedDataCrc).ToString().ToLowerInvariant()}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static bool IsPayloadName(string name) {
    var leaf = Path.GetFileName(name.Replace('/', Path.DirectorySeparatorChar));
    return leaf.Equals(UImageWriter.PayloadName, StringComparison.OrdinalIgnoreCase)
        || leaf.Equals(UImageWriter.DecompressedName, StringComparison.OrdinalIgnoreCase);
  }

  private static byte[] ReadAll(Stream stream) {
    if (!stream.CanRead)
      throw new ArgumentException("uImage operation requires a readable stream.", nameof(stream));
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static UImageReader.UImage ReadValidImage(ReadOnlySpan<byte> data) {
    var image = UImageReader.Read(data);
    if (image.HeaderCrc != image.ComputedHeaderCrc)
      throw new InvalidDataException("uImage: header CRC mismatch.");
    if (image.DataCrc != image.ComputedDataCrc)
      throw new InvalidDataException("uImage: data CRC mismatch.");
    return image;
  }

  private static UImageReader.LegacyHeader ReadValidatedHeader(Stream stream, bool validateDataCrc) {
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("uImage validation requires a readable, seekable stream.", nameof(stream));
    if (stream.Length < UImageReader.HeaderSize)
      throw new InvalidDataException($"uImage: file shorter than {UImageReader.HeaderSize}-byte header.");

    Span<byte> rawHeader = stackalloc byte[UImageReader.HeaderSize];
    stream.Position = 0;
    stream.ReadExactly(rawHeader);
    var header = UImageReader.ReadHeader(rawHeader);
    if (header.HeaderCrc != header.ComputedHeaderCrc)
      throw new InvalidDataException("uImage: header CRC mismatch.");

    var payloadEnd = UImageReader.HeaderSize + (long)header.DataSize;
    if (payloadEnd > stream.Length)
      throw new InvalidDataException(
        $"uImage: header declares {header.DataSize} payload bytes, but only {stream.Length - UImageReader.HeaderSize} are present.");

    if (validateDataCrc) {
      stream.Position = UImageReader.HeaderSize;
      var crc = Crc32Ieee.Compute(stream, header.DataSize);
      if (crc != header.DataCrc)
        throw new InvalidDataException("uImage: data CRC mismatch.");
    }

    return header;
  }

  private static void ValidateCandidate(Stream candidate)
    => _ = ReadValidatedHeader(candidate, validateDataCrc: true);

  private static void RequireEditable(Stream archive) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("uImage editing requires a readable, writable, seekable stream.", nameof(archive));
  }

  private static void CopyWhole(Stream input, Stream output) {
    if (ReferenceEquals(input, output)) return;
    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    input.CopyTo(output);
  }

  private static void CopyExactly(Stream input, Stream output, long count) {
    var buffer = new byte[64 * 1024];
    var remaining = count;
    while (remaining > 0) {
      var wanted = (int)Math.Min(buffer.Length, remaining);
      var read = input.Read(buffer, 0, wanted);
      if (read == 0)
        throw new EndOfStreamException($"uImage: expected {remaining} more bytes.");
      output.Write(buffer, 0, read);
      remaining -= read;
    }
  }
}
