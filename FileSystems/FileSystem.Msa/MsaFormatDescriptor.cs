#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Fat;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Msa;

public sealed class MsaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap {
  public string Id => "Msa";
  public string DisplayName => "MSA (Magic Shadow Archiver)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.CanModify;
  public string DefaultExtension => ".msa";
  public IReadOnlyList<string> Extensions => [".msa"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x0E, 0x0F], Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rle", "RLE")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Atari ST Magic Shadow Archiver disk image with RLE compression";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MsaReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "RLE", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MsaReader(stream);
    foreach (var e in r.Entries)
      WriteFile(outputDir, e.Name, r.Extract(e));
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();
    if (fileList.Count == 0) return;
    var (_, data) = fileList[0]; // First file is the raw disk image
    MsaWriter.Write(output, data);
  }

  /// <summary>
  /// Adds files to the FAT12 filesystem inside an existing MSA image. Each call
  /// performs decode → modify FAT → re-encode (see <see cref="MsaModifier"/>);
  /// per-track RLE compression makes anything cheaper architecturally impossible.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      MsaModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes files from the FAT12 filesystem inside an existing MSA image.
  /// Inner-layer wipe is delegated to <see cref="FileSystem.Fat.FatRemover"/>
  /// (zeros cluster bytes + cluster-tip slack + dirent + FAT entries), then the
  /// modified flat image is re-encoded to MSA tracks.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      MsaModifier.RemoveFile(archive, name);
  }

  // ── IArchiveDefragmentable ───────────────────────────────────────────

  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Defragments the inner FAT12 filesystem inside an MSA image. The image is
  /// decoded to a flat disk, the FAT layer is defragmented via rebuild (read all
  /// files, rebuild with FatWriter which always start-packs), and the result is
  /// re-encoded to MSA tracks preserving the original geometry.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    archive.Position = 0;
    var reader = new MsaReader(archive);
    if (reader.Entries.Count == 0) return;
    var flat = reader.Extract(reader.Entries[0]);
    var geom = (reader.SectorsPerTrack, reader.Sides, reader.StartTrack, reader.EndTrack);

    // Read all files from the inner FAT image.
    using var fatStream = new MemoryStream(flat, writable: false);
    var fatReader = new FatReader(fatStream);
    var files = fatReader.Entries
      .Where(e => !e.IsDirectory)
      .Select(e => (e.Name, fatReader.Extract(e)))
      .ToList();

    // Rebuild the FAT image (FatWriter always start-packs = defragmented).
    IReadOnlyList<(string Name, byte[] Data)> ordered = options.Mode switch {
      DefragMode.ConsolidateAtEnd => files.OrderByDescending(f => f.Item2.Length).ToList(),
      _ => files,
    };

    var fw = new FatWriter();
    foreach (var (name, data) in ordered) fw.AddFile(name, data);
    var totalSectors = flat.Length / 512;
    var rebuilt = fw.Build(totalSectors: totalSectors);
    if (rebuilt.Length != flat.Length) {
      var sized = new byte[flat.Length];
      Array.Copy(rebuilt, sized, Math.Min(rebuilt.Length, sized.Length));
      rebuilt = sized;
    }

    // Re-encode to MSA.
    using var ms = new MemoryStream();
    MsaWriter.Write(ms, rebuilt, geom.SectorsPerTrack, geom.Sides);
    var msaBytes = ms.ToArray();
    archive.Position = 0;
    archive.Write(msaBytes, 0, msaBytes.Length);
    archive.SetLength(msaBytes.Length);
  }

  // ── IFilesystemExtentMap ─────────────────────────────────────────────

  /// <summary>
  /// Decodes the MSA tracks to a flat FAT12 image and delegates to
  /// <see cref="FatExtentMap.Enumerate"/> for the actual cluster-chain walk.
  /// The returned offsets are relative to the inner flat image (not the MSA
  /// container) — this matches what the defrag window expects for filesystem
  /// extent maps.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    image.Position = 0;
    var reader = new MsaReader(image);
    if (reader.Entries.Count == 0) yield break;
    var flat = reader.Extract(reader.Entries[0]);
    using var fatStream = new MemoryStream(flat, writable: false);
    foreach (var extent in FatExtentMap.Enumerate(fatStream))
      yield return extent;
  }
}
