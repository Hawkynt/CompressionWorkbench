#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Os9Rbf;

/// <summary>
/// Read+write descriptor for Microware OS-9 RBF (Random-Block-File) disk
/// images. OS-9 was a multi-tasking real-time OS released in 1979 by Microware
/// Systems; it shipped on the Tandy CoCo, Sharp MZ-2500, embedded systems and
/// later as OS-9/68000 and OS-9000. The writer emits a 35-track DSDD CoCo
/// reference geometry (~315 KB); the reader parses any RBF image whose root
/// directory descriptor is reachable via the identification sector.
/// </summary>
public sealed class Os9RbfFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {

  public string Id => "Os9Rbf";
  public string DisplayName => "Microware OS-9 RBF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".os9";
  public IReadOnlyList<string> Extensions => [".os9", ".rbf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // RBF identification sectors have no fixed magic — detection is by extension
  // plus structural validation (DD.TOT, DD.DIR, DD.BIT plausibility) in the reader.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Microware OS-9 RBF disk image (35-track DSDD CoCo reference, ~315 KB, 256-byte sectors). " +
    "Files described by file-descriptor sectors with segment lists; root directory only.";

  public long? MaxTotalArchiveSize => Os9Layout.TotalBytes;
  public long? MinTotalArchiveSize => 0;
  public string AcceptedInputsDescription =>
    "ASCII filenames up to 28 characters; flat root directory; ~315 KB total payload.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "Flat root directory only; no subdirectories."; return false; }
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Length > Os9Layout.DirEntryNameMaxBytes - 1) {
      reason = $"OS-9 RBF filenames are limited to {Os9Layout.DirEntryNameMaxBytes - 1} characters.";
      return false;
    }
    foreach (var c in name) {
      if (c is < (char)0x20 or > (char)0x7E) {
        reason = "Filename contains non-printable ASCII characters.";
        return false;
      }
    }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var v = ReadVolume(stream);
    return v.Files.Select((f, i) => new ArchiveEntryInfo(
      i, f.Name, f.ByteLength, f.ByteLength, "stored",
      f.IsDirectory, false, f.Created)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var v = ReadVolume(stream);
    foreach (var f in v.Files) {
      if (f.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(f.Name, files)) continue;
      WriteFile(outputDir, f.Name, Os9RbfReader.Extract(v, f));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (Path.GetFileName(i.ArchiveName), File.ReadAllBytes(i.FullPath)))
      .ToList();
    var image = Os9RbfWriter.Build(files);
    output.Write(image);
  }

  private static Os9RbfReader.Volume ReadVolume(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Os9RbfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }
}
