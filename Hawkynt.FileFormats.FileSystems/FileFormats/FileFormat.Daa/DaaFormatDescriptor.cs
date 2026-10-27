#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Daa;

/// <summary>
/// PowerISO DAA ("Direct Access Archive") compressed CD/DVD image, format version 0x100: exposes
/// the ISO as <c>image.iso</c> plus a synthetic <c>metadata.ini</c>; creates and rewrites
/// version 0x100 images PowerISO reads. Version 0x110, split, password-protected and gBurner GBI
/// images are refused.
/// </summary>
/// <remarks>Specification: <c>docs/DAA-ON-DISK.md</c>.</remarks>
public sealed class DaaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, ISyntheticEntryNames {

  public const string ImageName = "image.iso";
  public const string MetadataName = "metadata.ini";
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataName };

  public string Id => "Daa";
  public string DisplayName => "DAA (PowerISO)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".daa";
  public IReadOnlyList<string> Extensions => [".daa"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("DAA\0\0\0\0\0\0\0\0\0\0\0\0\0"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate chunks")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PowerISO DAA compressed ISO image (format 0x100, deflate chunks)";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  /// <summary>DAA requires an ISO image payload, so it has no empty container for purge to leave behind.</summary>
  public bool CanPurgeToEmpty => false;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var image = DaaImage.Read(stream);
      var packed = image.Chunks.Sum(c => (long)c.Length);
      var metadata = Metadata(image);
      return [
        new(0, MetadataName, metadata.Length, metadata.Length, "stored", false, false, null),
        new(1, ImageName, image.IsoSize, packed, "deflate", false, false, null),
      ];
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.daa", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var image = DaaImage.Read(stream);
    if (files is not { Length: > 0 } || MatchesFilter(MetadataName, files))
      WriteFile(outputDir, MetadataName, Metadata(image));
    if (files is not { Length: > 0 } || MatchesFilter(ImageName, files)) {
      using var target = CreateEntryFile(outputDir, ImageName);
      image.CopyIsoTo(stream, target);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    DaaImage.Write(output, SingleImage(inputs) ?? throw new ArgumentException("DAA needs the ISO image to store.", nameof(inputs)));
  }

  /// <summary>Replaces <c>image.iso</c>, keeping the chunk size.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    var existing = DaaImage.Read(archive);
    var replacement = SingleImage(inputs);
    if (replacement is null) return;
    WholeImageRebuildCommitter.Replace(archive,
      candidate => DaaImage.Write(candidate, replacement, (int)existing.ChunkSize),
      candidate => DaaImage.Read(candidate).CopyIsoTo(candidate, Stream.Null));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Length > 0)
      throw new NotSupportedException("DAA holds exactly one image; it can be replaced but not removed.");
  }

  private static byte[] Metadata(DaaImage i) => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
    $"[daa]\nversion=0x{i.Version:X}\nchunk_size={i.ChunkSize}\nchunks={i.Chunks.Count}\niso_size={i.IsoSize}\n"));

  private static byte[]? SingleImage(IReadOnlyList<ArchiveInputInfo> inputs) {
    byte[]? image = null;
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (Path.GetFileName(input.ArchiveName.Replace('\\', '/')).Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) continue;
      if (image is not null) throw new ArgumentException("DAA holds exactly one image.", nameof(inputs));
      image = input.ReadContent();
    }
    return image;
  }
}
