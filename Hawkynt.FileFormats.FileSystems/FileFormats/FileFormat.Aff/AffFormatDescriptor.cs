#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Aff;

/// <summary>
/// AFF (Advanced Forensic Format, AFFLIB v3) single-file evidence image. Exposes the acquired
/// media as <c>disk.img</c>, verified against the stored md5/sha1/sha256, and the acquisition
/// metadata as a synthetic <c>metadata.ini</c>. Creating writes zlib/ZERO pages that AFFLIB's
/// tools accept; replacing <c>disk.img</c> rewrites the pages and hashes and keeps every
/// acquisition segment. Encrypted images are listed but refused; signed images are not
/// modified (their signatures would no longer verify). AFD (directory) and AFM (raw plus
/// sidecar) are multi-file layouts and outside this single-stream descriptor.
/// </summary>
/// <remarks>Specification: <c>docs/AFF-ON-DISK.md</c>.</remarks>
public sealed class AffFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, ISyntheticEntryNames {

  public const string DiskName = "disk.img";
  public const string MetadataName = "metadata.ini";
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataName };

  public string Id => "Aff";
  public string DisplayName => "AFF (Advanced Forensic Format)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".aff";
  public IReadOnlyList<string> Extensions => [".aff"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("AFF10\r\n\0"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zlib", "zlib pages"), new("lzma", "LZMA pages (read)"), new("zero", "zero pages")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "AFFLIB v3 Advanced Forensic Format evidence image (zlib/LZMA/zero pages, md5/sha1 verified)";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    AffImage image;
    try {
      image = AffImage.Read(stream);
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [
        new(0, "FULL.aff", length, length, "stored", false, false, null),
        new(1, MetadataName, 0, 0, "stored", false, false, null),
      ];
    }
    var metadata = image.RenderMetadata();
    var packed = image.Segments.Where(s => AffImage.TryPageNumber(s.Name, out _)).Sum(s => (long)s.Data.Length);
    return [
      new(0, MetadataName, metadata.Length, metadata.Length, "stored", false, false, null),
      new(1, DiskName, image.ImageSize, packed, "aff", false, image.IsEncrypted, null),
    ];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var image = AffImage.Read(stream);
    if (files is not { Length: > 0 } || MatchesFilter(MetadataName, files))
      WriteFile(outputDir, MetadataName, image.RenderMetadata());
    if (files is not { Length: > 0 } || MatchesFilter(DiskName, files))
      WriteFile(outputDir, DiskName, image.ReadVerifiedImage());
  }

  /// <summary>Writes a fresh AFF from the single image input (<c>disk.img</c> or any one file).</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    AffImage.Write(output, SingleImage(inputs) ?? throw new ArgumentException("AFF needs the image to store (disk.img).", nameof(inputs)));
  }

  /// <summary>Replaces <c>disk.img</c>; acquisition segments are kept, pages and hashes regenerated.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var image = AffImage.Read(archive);
    if (image.IsEncrypted || image.IsSigned)
      throw new NotSupportedException("AFF: encrypted or signed images are not rewritten (keys/signatures cannot be regenerated).");
    var replacement = SingleImage(inputs);
    if (replacement is null)
      return;
    var keep = image.Segments.Where(s => !AffImage.IsDerived(s.Name)).ToList();
    var pageSize = image.PageSize is > 0 and var p ? (int)p : AffImage.DefaultPageSize;
    WholeImageRebuildCommitter.Replace(archive,
      candidate => AffImage.Write(candidate, replacement, keep, pageSize, image.SectorSize),
      candidate => AffImage.Read(candidate).ReadVerifiedImage());
  }

  /// <summary>The image itself cannot be removed from an evidence container.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Length > 0)
      throw new NotSupportedException("AFF holds exactly one image; it can be replaced but not removed.");
  }

  private static byte[]? SingleImage(IReadOnlyList<ArchiveInputInfo> inputs) {
    byte[]? image = null;
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (Path.GetFileName(input.ArchiveName.Replace('\\', '/')).Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) continue;
      if (image is not null) throw new ArgumentException("AFF holds exactly one image.", nameof(inputs));
      image = input.ReadContent();
    }
    return image;
  }
}
