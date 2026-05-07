#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Udf;

public sealed class UdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveModifiable {
  // WORM write constraints — UDF has no inherent ceiling; minimum viable image ~1 MB.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 1 * 1024 * 1024;
  public string AcceptedInputsDescription => "UDF 2.01 disc image; any files, flat directory.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  public string Id => "Udf";
  public string DisplayName => "UDF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Adds (or replaces) files at the root of an existing UDF image. Uses
  /// <see cref="UdfModifier"/> for true random-access I/O — only the
  /// Partition Descriptor sector, the root directory's File Entry sector,
  /// the FID extent, and the new file's FE + data sectors are touched.
  /// The 32 KiB system area, VRS, AVDP, LVD, and FSD are left untouched.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs))
      UdfModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing UDF image. Uses
  /// <see cref="UdfModifier"/> for O(touched bytes) random-access I/O — the
  /// FID's deleted flag (ECMA-167 §14.4.3 bit 2) is set, its identifier
  /// bytes are zeroed, the tag is re-CRC'd, and the file's FE and data
  /// extents are zero-wiped.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      UdfModifier.RemoveFile(archive, name, wipeData: true);
  }

  public string DefaultExtension => ".udf";
  public IReadOnlyList<string> Extensions => [".udf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("NSR02"u8.ToArray(), Offset: 0x8001, Confidence: 0.90),
    new("NSR03"u8.ToArray(), Offset: 0x8001, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Universal Disk Format";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new UdfReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new UdfReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new UdfWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
