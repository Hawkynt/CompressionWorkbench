#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Msa;

public sealed class MsaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
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
}
