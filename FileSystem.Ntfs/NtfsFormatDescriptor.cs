#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ntfs;

public sealed class NtfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Ntfs";
  public string DisplayName => "NTFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ntfs";
  public IReadOnlyList<string> Extensions => [".ntfs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'N', (byte)'T', (byte)'F', (byte)'S', (byte)' ', (byte)' ', (byte)' ', (byte)' '], Offset: 3, Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// NTFS filesystem image with LZNT1 compression support. The writer emits
  /// every reserved system MFT record (0-15) with real content: $MFT,
  /// $MFTMirr, $LogFile, $Volume (with a version-3.1 $VOLUME_INFORMATION
  /// and a $VOLUME_NAME), $AttrDef, root ., $Bitmap, $Boot, $BadClus,
  /// $Secure, $UpCase (128 KiB UTF-16 table), and $Extend. Every record
  /// carries $STANDARD_INFORMATION and $FILE_NAME, the Update Sequence
  /// Array (USA) fixup is applied at sector boundaries, and the on-disk
  /// cluster bitmap reflects actual allocations.
  /// </summary>
  public string Description => "NTFS filesystem image with LZNT1 compression and full $MFT system files";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new NtfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new NtfsWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new NtfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Add files to an existing NTFS image. Current implementation re-builds the image
  /// with all existing files + the new ones — the inherent build-from-scratch design
  /// of <see cref="NtfsWriter"/> means "add" equals "re-pack" here. Use
  /// <see cref="Remove"/> first to clean up stale entries.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    archive.Position = 0;
    var reader = new NtfsReader(archive);
    var combined = new NtfsWriter();
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      combined.AddFile(entry.Name, reader.Extract(entry));
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      combined.AddFile(name, data);
    var totalSize = (int)archive.Length;
    var rebuilt = combined.Build(totalSize);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Removes files from an existing NTFS image with full secure wipe (cluster bytes
  /// for non-resident data, MFT record, and root-dir index entry). No forensic
  /// recovery of the removed content is possible from the resulting bytes.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var name in entryNames)
      NtfsRemover.Remove(image, name);
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }
}
