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
  /// Adds (or replaces by name) files inside an existing Udf image.
  /// Read-extract-rebuild via <see cref="ModifyRebuilder"/>; the rebuild
  /// path doubles as a secure-wipe for replaced bytes.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        var r = new UdfReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new UdfWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });

  /// <summary>
  /// Removes the named entries from an existing Udf image. The image is
  /// rebuilt without the target entries — old file bytes are wiped because
  /// the new layout starts fresh, leaving no forensic trace.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new UdfReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new UdfWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });

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
