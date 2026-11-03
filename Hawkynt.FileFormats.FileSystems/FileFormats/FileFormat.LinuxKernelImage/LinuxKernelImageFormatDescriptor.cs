#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.FirmwareCommon;

namespace FileFormat.LinuxKernelImage;

/// <summary>
/// Linux kernel boot image — x86 <c>bzImage</c>, ARM <c>zImage</c>, arm64 / RISC-V <c>Image</c>,
/// EFI <c>zboot</c> — as a read-only pseudo-archive: <c>metadata.ini</c> carries the boot-protocol
/// header and the version banner, and the members are the parts the protocol defines plus the
/// decompressed kernel where the payload decodes (<c>vmlinux</c> for x86, <c>Image</c> otherwise).
/// </summary>
/// <remarks>
/// Read-only: a kernel image is produced by the kernel build (the decompressor is code linked
/// around the payload), so there is nothing a container writer could honestly re-create.
/// </remarks>
public sealed class LinuxKernelImageFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "LinuxKernelImage";
  public string DisplayName => "Linux kernel image (zImage/Image)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".zimage";
  public IReadOnlyList<string> Extensions => [".zimage", ".bzimage", ".vmlinuz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("HdrS"u8.ToArray(), Offset: 0x202, Confidence: 0.93),
    new([0x18, 0x28, 0x6F, 0x01], Offset: 0x24, Confidence: 0.9),
    new("ARMd"u8.ToArray(), Offset: 0x38, Confidence: 0.9),
    new("RSC\x05"u8.ToArray(), Offset: 0x38, Confidence: 0.9),
    new("zimg"u8.ToArray(), Offset: 4, Confidence: 0.92),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Linux kernel boot image (x86 bzImage, ARM zImage, arm64/RISC-V Image, EFI zboot): boot header, setup and " +
    "payload parts, and the decompressed kernel when its payload decodes. Read-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => FirmwareArchive.List(Entries(FirmwareArchive.ReadAll(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(FirmwareArchive.ReadAll(stream)), outputDir, files);

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) => FirmwareArchive.List(Entries(archive.ToArray()));

  void IArchiveFormatOperations.ExtractSpan(ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(archive.ToArray()), outputDir, files);

  public static List<FirmwareEntry> Entries(byte[] data) {
    LinuxKernelImage img;
    try {
      img = LinuxKernelImage.Parse(data);
    } catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException) {
      return [new(FirmwareArchive.MetadataName, Encoding.UTF8.GetBytes($"[linux_kernel]\nparse_status = partial\nerror = {e.Message}\n")), new("FULL.bin", data)];
    }
    var sb = new StringBuilder("[linux_kernel]\n");
    sb.Append("kind = ").Append(img.Kind).Append('\n');
    foreach (var (key, value) in img.Fields) sb.Append(key).Append(" = ").Append(value.ReplaceLineEndings(" ")).Append('\n');
    sb.Append("parse_status = ok\n");
    return [new(FirmwareArchive.MetadataName, Encoding.UTF8.GetBytes(sb.ToString())), .. img.Parts];
  }
}
