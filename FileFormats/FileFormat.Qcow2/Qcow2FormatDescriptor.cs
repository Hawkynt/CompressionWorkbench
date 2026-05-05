#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Qcow2;

public sealed class Qcow2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Qcow2";
  public string DisplayName => "QCOW2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest;
  public string DefaultExtension => ".qcow2";
  public IReadOnlyList<string> Extensions => [".qcow2", ".qcow"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x51, 0x46, 0x49, 0xFB], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("qcow2", "QCOW2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "QEMU Copy-On-Write disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Qcow2Reader(stream);
    return [new ArchiveEntryInfo(0, "disk.img", r.VirtualSize, stream.Length, "QCOW2", false, false, null)];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Qcow2Reader(stream);
    WriteFile(outputDir, "disk.img", r.ExtractDisk());
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: builds a FAT filesystem from the input files, then wraps it in a
    // QCOW2 v2 container. This makes the disk image mountable and the files
    // recoverable by any OS that reads FAT.
    var fatImage = FileSystem.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    var w = new Qcow2Writer();
    w.SetDiskImage(fatImage);
    w.WriteTo(output);
  }
}
