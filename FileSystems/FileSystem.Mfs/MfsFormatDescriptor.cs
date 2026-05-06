#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Mfs;

public sealed class MfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Mfs";
  public string DisplayName => "MFS (Macintosh File System)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Mfs image.
  /// Read-extract-rebuild via <see cref="ModifyRebuilder"/>; the rebuild
  /// path doubles as a secure-wipe for replaced bytes.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        var r = new MfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new MfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  /// <summary>
  /// Removes the named entries from an existing Mfs image. The image is
  /// rebuilt without the target entries — old file bytes are wiped because
  /// the new layout starts fresh, leaving no forensic trace.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new MfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new MfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  public string DefaultExtension => ".mfs";
  public IReadOnlyList<string> Extensions => [".mfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0xD2, 0xD7], Offset: 1024, Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Classic Macintosh MFS filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new MfsWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MfsReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
