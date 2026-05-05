#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Rt11;

/// <summary>
/// Read+write descriptor for DEC RT-11 disk images. RT-11 was DEC's flagship
/// PDP-11 single-user operating system from 1973 onwards and remains the most
/// common filesystem found on PDP-11 disk image dumps. Files are 6.3 RAD-50
/// encoded names stored contiguously in 512-byte blocks; the writer emits a
/// canonical RX01 single-density 8" floppy image (~256 KB).
/// </summary>
public sealed class Rt11FormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {

  public string Id => "Rt11";
  public string DisplayName => "DEC RT-11 (RX01)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rt11";
  public IReadOnlyList<string> Extensions => [".rt11", ".rx01"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Detection by the home-block "DECRT11A    " ASCII marker at file offset
  // 1*512 + 0x1F0 = 0x3F0.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("DECRT11A    "u8.ToArray(), Offset: 0x3F0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "DEC RT-11 disk image (RX01 8\" SSSD reference geometry, 256 256 bytes). " +
    "Flat directory at block 6, 6.3 RAD-50 filenames, files stored contiguously in 512-byte blocks.";

  public long? MaxTotalArchiveSize => Rt11Layout.ImageBytes;
  public long? MinTotalArchiveSize => 0;
  public string AcceptedInputsDescription =>
    $"6.3 RAD-50 filenames (A-Z, 0-9, $, .); up to {Rt11Layout.EntriesPerSegment - 1} files per directory segment; ~250 KB total payload.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "RT-11 has a single flat directory; no subdirectories."; return false; }
    var fileName = Path.GetFileName(input.ArchiveName);
    var dot = fileName.LastIndexOf('.');
    var stem = dot < 0 ? fileName : fileName[..dot];
    var ext = dot < 0 ? "" : fileName[(dot + 1)..];
    if (stem.Length > 6) { reason = "Filename stem exceeds 6 characters."; return false; }
    if (ext.Length > 3) { reason = "Extension exceeds 3 characters."; return false; }
    if (!Rad50.IsValid(stem) || !Rad50.IsValid(ext)) {
      reason = "Filename contains characters outside RAD-50 alphabet (A-Z, 0-9, $, .).";
      return false;
    }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var v = ReadVolume(stream);
    return v.Files.Select((f, i) => new ArchiveEntryInfo(
      i, f.Name, f.ByteLength, f.ByteLength, "stored",
      false, false, f.Created)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var v = ReadVolume(stream);
    foreach (var f in v.Files) {
      if (files != null && files.Length > 0 && !MatchesFilter(f.Name, files)) continue;
      WriteFile(outputDir, f.Name, Rt11Reader.Extract(v, f));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (Path.GetFileName(i.ArchiveName), File.ReadAllBytes(i.FullPath)))
      .ToList();
    var image = Rt11Writer.Build(files);
    output.Write(image);
  }

  private static Rt11Reader.Volume ReadVolume(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Rt11Reader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }
}
