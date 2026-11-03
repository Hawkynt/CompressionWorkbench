#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Isz;

/// <summary>
/// UltraISO ISZ ("ISO Zipped") compressed CD/DVD image: exposes the decompressed ISO as
/// <c>image.iso</c> (CRC-32 verified) plus a synthetic <c>metadata.ini</c>. Creates single-segment
/// images with zero/stored/zlib chunks; replacing <c>image.iso</c> rewrites the container.
/// Encrypted and split (.i01…) images are refused.
/// </summary>
/// <remarks>Specification: EZB Systems' ISZ 1.00 and <c>docs/ISZ-ON-DISK.md</c>.</remarks>
public sealed class IszFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, ISyntheticEntryNames {

  public const string ImageName = "image.iso";
  public const string MetadataName = "metadata.ini";
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataName };

  public string Id => "Isz";
  public string DisplayName => "ISZ (UltraISO)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".isz";
  public IReadOnlyList<string> Extensions => [".isz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("IsZ!"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zlib", "zlib chunks"), new("bzip2", "bzip2 chunks (read)")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "UltraISO ISZ compressed ISO image (zlib/bzip2 chunks, CRC-32 verified)";
  /// <summary>An ISZ requires its image member, so purge cannot leave an empty container.</summary>
  public bool CanPurgeToEmpty => false;
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var image = IszImage.Read(stream);
      var packed = image.Chunks.Where(c => c.Kind != IszImage.ChunkKind.Zero).Sum(c => (long)c.Length);
      var metadata = Metadata(image);
      return [
        new(0, MetadataName, metadata.Length, metadata.Length, "stored", false, false, null),
        new(1, ImageName, image.ImageSize, packed, "isz", false, false, null),
      ];
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.isz", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var image = IszImage.Read(stream);
    if (files is not { Length: > 0 } || MatchesFilter(MetadataName, files))
      WriteFile(outputDir, MetadataName, Metadata(image));
    if (files is not { Length: > 0 } || MatchesFilter(ImageName, files)) {
      using var target = CreateEntryFile(outputDir, ImageName);
      image.CopyImageTo(stream, target);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    IszImage.Write(output, SingleImage(inputs) ?? throw new ArgumentException("ISZ needs the ISO image to store.", nameof(inputs)));
  }

  /// <summary>Replaces <c>image.iso</c>, keeping the chunk size and volume serial of the existing container.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    var existing = IszImage.Read(archive);
    var replacement = SingleImage(inputs);
    if (replacement is null) return;
    WholeImageRebuildCommitter.Replace(archive,
      candidate => IszImage.Write(candidate, replacement, (int)existing.BlockSize, existing.SectorSize, existing.VolumeSerial),
      candidate => IszImage.Read(candidate).CopyImageTo(candidate, Stream.Null));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Length > 0)
      throw new NotSupportedException("ISZ holds exactly one image; it can be replaced but not removed.");
  }

  private static byte[] Metadata(IszImage i) => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
    $"[isz]\nversion={i.Version}\nvolume_serial=0x{i.VolumeSerial:X8}\nsector_size={i.SectorSize}\ntotal_sectors={i.TotalSectors}\nchunk_size={i.BlockSize}\nchunks={i.Chunks.Count}\ndata_crc32_register=0x{i.DataChecksum:X8}\n"));

  private static byte[]? SingleImage(IReadOnlyList<ArchiveInputInfo> inputs) {
    byte[]? image = null;
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (Path.GetFileName(input.ArchiveName.Replace('\\', '/')).Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) continue;
      if (image is not null) throw new ArgumentException("ISZ holds exactly one image.", nameof(inputs));
      image = input.ReadContent();
    }
    return image;
  }
}
