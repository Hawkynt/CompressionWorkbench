#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Acronis;

/// <summary>
/// Format descriptor for Acronis True Image classic .tib backups (read-only listing).
/// </summary>
/// <remarks>
/// <para>
/// Detects by magic <c>CE 24 B9 A2</c> (LE 0xA2B924CE) at offset 0. Surfaces file names
/// + sizes via the Listing record stream documented in the upstream RE
/// (https://github.com/dennisss/acronis-tib). Extraction is NOT supported: the
/// FirstFileMetaRecord/FileMetaA/B/C records that bridge a listing entry to its
/// per-file Blob RecordIndex remain undocumented upstream, so we cannot map a file
/// to its data without risking incorrect content.
/// </para>
/// <para>
/// Out of scope for this descriptor: encrypted .tib backups, sector-by-sector slices,
/// multi-volume slice chains, and the .tibx format (Acronis True Image 2020+).
/// </para>
/// </remarks>
public sealed class AcronisFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "AcronisTib";
  public string DisplayName => "Acronis True Image (.tib)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".tib";
  public IReadOnlyList<string> Extensions => [".tib"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0xCE, 0x24, 0xB9, 0xA2], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate (record stream)")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Acronis True Image classic .tib backup — read-only listing (file extraction blocked by undocumented per-file meta records)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var r = new AcronisReader(stream);
    return r.Entries.Select((e, i) => {
      var full = string.IsNullOrEmpty(e.Path)
        ? e.Name
        : e.Path.TrimEnd('/', '\\') + "/" + e.Name;
      return new ArchiveEntryInfo(
        Index: i,
        Name: full,
        OriginalSize: e.FileSize,
        CompressedSize: e.FileSize,
        Method: "Deflate",
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: e.Time,
        Kind: null
      );
    }).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    // Honest fallback: the upstream RE marks the FirstFileMetaRecord/FileMetaA/B/C records
    // that bridge a Listing entry to its per-file RecordIndex as "no serious parser yet", so we
    // cannot reliably map a file to its blob data. Refuse rather than write incorrect content.
    throw new NotSupportedException(
      "Acronis classic .tib extraction is not supported: the FirstFileMetaRecord/FileMetaA/B/C bridge between a listed file and its data blocks is undocumented in the upstream RE (https://github.com/dennisss/acronis-tib). Listing is available via List().");
  }
}
