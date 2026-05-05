using System.Globalization;
using System.Text;
using Compression.Registry;
using FileSystem.SquashFs;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AppImage;

/// <summary>
/// Descriptor for Linux AppImage executables.
/// An AppImage is an ELF stub followed immediately (or after alignment padding) by
/// a SquashFS v4 filesystem image holding the application payload. Types 1 and 2
/// are distinguished by the magic bytes <c>AI\x01</c> / <c>AI\x02</c> placed at
/// ELF offset 8 (inside the <c>EI_PAD</c> region of <c>e_ident</c>).
/// The descriptor surfaces:
/// <list type="bullet">
///   <item>A synthetic <c>metadata.ini</c> with AppImage type, runtime offset, and architecture.</item>
///   <item>Every SquashFS entry, prefixed with <c>filesystem/</c>.</item>
/// </list>
/// </summary>
public sealed class AppImageFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>Unique format identifier.</summary>
  public string Id => "AppImage";

  /// <summary>Human-readable name.</summary>
  public string DisplayName => "AppImage";

  /// <summary>This format describes an archive container.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Capabilities supported by this descriptor.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <summary>Preferred extension (case-sensitive on Linux filesystems).</summary>
  public string DefaultExtension => ".AppImage";

  /// <summary>Extensions recognised as AppImage files.</summary>
  public IReadOnlyList<string> Extensions => [".AppImage", ".appimage"];

  /// <summary>Compound extensions are not used by this format.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// Magic-byte signatures. The AppImage spec puts <c>AI\x01</c> or <c>AI\x02</c>
  /// at file offset 8 — inside the ELF <c>EI_PAD</c> area. Either marker combined
  /// with the leading <c>\x7FELF</c> uniquely identifies an AppImage.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'A', (byte)'I', 0x02], Offset: 8, Confidence: 0.97),
    new([(byte)'A', (byte)'I', 0x01], Offset: 8, Confidence: 0.93),
  ];

  /// <summary>Method labels are filled in at read time from the SquashFS compression id.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("squashfs", "SquashFS")];

  /// <summary>Not a TAR-compound format.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Algorithmic family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Short description.</summary>
  public string Description =>
    "Linux AppImage (ELF runtime + appended SquashFS filesystem)";

  /// <summary>
  /// Lists a synthetic <c>metadata.ini</c> entry plus every SquashFS entry from
  /// the appended filesystem, each prefixed with <c>filesystem/</c>.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    _ = password;
    var info = AppImageLocator.Locate(stream);
    var fsStream = new OffsetSubStream(stream, info.SquashFsOffset,
      info.FileLength - info.SquashFsOffset, leaveOpen: true);
    using var r = new SquashFsReader(fsStream);

    var metadata = BuildMetadata(info, r.Entries.Count);

    var entries = new List<ArchiveEntryInfo> {
      new(0, "metadata.ini", metadata.Length, metadata.Length, "stored", false, false, null),
    };
    for (var i = 0; i < r.Entries.Count; i++) {
      var e = r.Entries[i];
      entries.Add(new ArchiveEntryInfo(
        i + 1, "filesystem/" + e.FullPath, e.Size, -1,
        "squashfs", e.IsDirectory, false, e.ModifiedTime));
    }
    return entries;
  }

  /// <summary>
  /// Extracts the synthetic <c>metadata.ini</c> plus every SquashFS entry
  /// to <paramref name="outputDir"/>, retaining the <c>filesystem/</c> prefix.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    _ = password;
    var info = AppImageLocator.Locate(stream);
    var fsStream = new OffsetSubStream(stream, info.SquashFsOffset,
      info.FileLength - info.SquashFsOffset, leaveOpen: true);
    using var r = new SquashFsReader(fsStream);

    var metadata = BuildMetadata(info, r.Entries.Count);
    if (files == null || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", metadata);

    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (e.IsSymlink) continue;
      var outName = "filesystem/" + e.FullPath;
      if (files != null && !MatchesFilter(outName, files)) continue;
      WriteFile(outputDir, outName, r.Extract(e));
    }
  }

  private static byte[] BuildMetadata(AppImageLocator.Info info, int fsEntries) {
    var sb = new StringBuilder();
    sb.Append("[appimage]\n");
    sb.Append(CultureInfo.InvariantCulture, $"type = {info.AppImageType}\n");
    sb.Append(CultureInfo.InvariantCulture, $"squashfs_offset = {info.SquashFsOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"runtime_size = {info.SquashFsOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"payload_size = {info.FileLength - info.SquashFsOffset}\n");
    sb.Append("[elf]\n");
    sb.Append(CultureInfo.InvariantCulture, $"class = {(info.ElfClass == 2 ? "ELF64" : "ELF32")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"endian = {(info.ElfData == 1 ? "little" : "big")}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"machine = {AppImageLocator.MachineName(info.Machine)} ({info.Machine})\n");
    sb.Append("[filesystem]\n");
    sb.Append(CultureInfo.InvariantCulture, $"kind = SquashFS\n");
    sb.Append(CultureInfo.InvariantCulture, $"entry_count = {fsEntries}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
