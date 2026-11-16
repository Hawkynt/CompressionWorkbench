#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Shared pseudo-archive surface of the DOS executable packers: a packed program lists as
/// <c>unpacked.exe</c> (the restored original) plus <c>metadata.ini</c>. A file that carries the
/// packer's marks but cannot be restored lists as <c>metadata.ini</c> with
/// <c>parse_status = partial</c> and the untouched <c>packed_payload.bin</c>; a file without them
/// is refused with <see cref="InvalidDataException"/>.
/// </summary>
public abstract class DosPackedExeDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>The unpacker behind this descriptor.</summary>
  protected abstract IDosExeUnpacker Unpacker { get; }

  /// <summary>Section name in <c>metadata.ini</c>.</summary>
  protected abstract string MetadataSection { get; }

  /// <summary>
  /// A looser recognition for files whose stub is not one the unpacker knows (e.g. the packer's
  /// copyright text); returns a description of the mark or null.
  /// </summary>
  protected virtual string? FindMarker(ReadOnlySpan<byte> file) => null;

  public abstract string Id { get; }
  public abstract string DisplayName { get; }
  public abstract string Description { get; }
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

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    this.BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in this.BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var bytes = ms.ToArray();
    // COM-capable unpackers recognise their stub in files without an MZ header.
    var variant = this.Unpacker.Detect(bytes);
    if (variant == null && !PackerScanner.IsMzExecutable(bytes))
      throw new InvalidDataException($"{this.DisplayName}: not a DOS MZ executable.");
    var marker = variant == null ? this.FindMarker(bytes) : null;
    if (variant == null && marker == null)
      throw new InvalidDataException($"{this.DisplayName}: no stub of this packer found.");

    if (variant != null) {
      try {
        var unpacked = this.Unpacker.Unpack(bytes);
        return [
          (unpacked.FileName, unpacked.Executable),
          ("metadata.ini", this.Metadata(unpacked.Variant, null, bytes.Length, unpacked.Executable.Length, null)),
        ];
      } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException) {
        return [
          ("metadata.ini", this.Metadata(variant, null, bytes.Length, null, ex.Message)),
          ("packed_payload.bin", bytes),
        ];
      }
    }

    return [
      ("metadata.ini", this.Metadata(null, marker, bytes.Length, null, "stub not recognised; the program is left packed")),
      ("packed_payload.bin", bytes),
    ];
  }

  private byte[] Metadata(string? variant, string? marker, long packedLength, long? unpackedLength, string? failure) {
    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $"[{this.MetadataSection}]\n");
    if (variant != null) sb.Append(CultureInfo.InvariantCulture, $"variant = {variant}\n");
    if (marker != null) sb.Append(CultureInfo.InvariantCulture, $"marker = {marker}\n");
    sb.Append(CultureInfo.InvariantCulture, $"packed_size = {packedLength}\n");
    if (unpackedLength is { } u) sb.Append(CultureInfo.InvariantCulture, $"unpacked_size = {u}\n");
    sb.Append(failure == null ? "parse_status = full\n" : $"parse_status = partial\nerror = {failure}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
