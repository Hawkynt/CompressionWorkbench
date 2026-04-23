#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Cpm;

/// <summary>
/// Read+write descriptor for CP/M 2.2 disk images using the 8" SSSD reference
/// geometry (256 256 bytes, 2 reserved tracks, 1024-byte blocks, 64 directory
/// entries). Kaypro/Osborne/Amstrad and other manufacturer-specific geometries
/// are not emitted by the writer; the reader still parses any image that
/// matches this layout.
/// </summary>
public sealed class CpmFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {

  public string Id => "Cpm";
  public string DisplayName => "CP/M 2.2 (8\" SSSD)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".cpm";
  public IReadOnlyList<string> Extensions => [".cpm", ".dsk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // CP/M disks have no magic — only geometry — so we advertise no magic-byte
  // signature. Detection falls back to extension-based matching.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "CP/M 2.2 disk image (8\" SSSD canonical geometry) — 77 tracks × 26 sectors × 128 B, " +
    "1024-byte allocation blocks, 64-entry directory, 8.3 filenames.";

  // Write constraints.
  public long? MaxTotalArchiveSize => CpmLayout.UsableBlocks * (long)CpmLayout.BlockSize;
  public long? MinTotalArchiveSize => 0;
  public string AcceptedInputsDescription =>
    $"Up to {CpmLayout.DirectoryEntries} directory entries, {CpmLayout.UsableBlocks} × 1024-byte blocks of data; 8.3 filenames.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "CP/M volumes have a single flat directory — no subdirectories."; return false; }
    var file = Path.GetFileName(input.ArchiveName);
    var dot = file.LastIndexOf('.');
    var name = dot < 0 ? file : file[..dot];
    var ext = dot < 0 ? "" : file[(dot + 1)..];
    if (name.Length > 8) { reason = "Filename stem exceeds 8 characters."; return false; }
    if (ext.Length > 3) { reason = "Extension exceeds 3 characters."; return false; }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var v = ReadVolume(stream);
    return v.Files.Select((f, i) => new ArchiveEntryInfo(
      i, f.FullName, f.Data.LongLength, f.Data.LongLength, "stored",
      false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var v = ReadVolume(stream);
    foreach (var f in v.Files) {
      if (files != null && files.Length > 0 && !MatchesFilter(f.FullName, files)) continue;
      WriteFile(outputDir, f.FullName, f.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (i.ArchiveName, File.ReadAllBytes(i.FullPath), (byte)0))
      .ToList();
    var image = CpmWriter.Build(files);
    output.Write(image);
  }

  private static CpmReader.Volume ReadVolume(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return CpmReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }
}
