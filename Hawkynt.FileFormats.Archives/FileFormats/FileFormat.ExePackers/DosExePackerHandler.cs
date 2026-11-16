#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// <see cref="IExecutablePackerHandler"/> over an <see cref="IDosExeUnpacker"/>: detects the
/// packer's stub in a DOS MZ file and rebuilds the original program as <c>unpacked.exe</c>.
/// </summary>
public sealed class DosExePackerHandler(IDosExeUnpacker unpacker) : IExecutablePackerHandler {
  public string Id => unpacker.Id;
  public string DisplayName => unpacker.DisplayName;

  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsX86;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var variant = unpacker.Detect(image);
    return variant == null
      ? new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, $"{this.DisplayName} stub not found.", true)])
      : new(true, this.Id, 0.95, []);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var exe = DosExe.TryRead(image);
    var info = exe == null
      ? new ExecutableImageInfo(ExecutableContainerKind.DosCom, CpuArchitecture.X86, 0x100, 0x100,
        [new("image", 0, (ulong)image.Length, 0x100, (ulong)image.Length, ExecutableRegionFlags.Read | ExecutableRegionFlags.Write | ExecutableRegionFlags.Execute, null, null)],
        [], [], [])
      : new ExecutableImageInfo(ExecutableContainerKind.DosMz, CpuArchitecture.X86, 0, (ulong)Math.Max(0, exe.EntryPoint),
        [new("load image", (ulong)exe.StartOfCode, (ulong)Math.Max(0, exe.EndOfCode - exe.StartOfCode), 0,
          (ulong)Math.Max(0, exe.EndOfCode - exe.StartOfCode), ExecutableRegionFlags.Read | ExecutableRegionFlags.Write | ExecutableRegionFlags.Execute, null, null)],
        [], [], []);
    var metadata = new Dictionary<string, string> {
      ["packer"] = this.Id,
      ["container"] = info.Container.ToString(),
    };
    if (unpacker.Detect(image) is { } variant)
      metadata["variant"] = variant;
    return new(this.Id, image.ToArray(), detection, info, this.Capabilities, metadata);
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    try {
      var unpacked = unpacker.Unpack(packed.OriginalImage);
      if (unpacked.Executable.LongLength > options.MaximumDecompressedSize)
        throw new InvalidDataException("Restored program exceeds the configured size limit.");
      var ini = new StringBuilder();
      ini.Append(CultureInfo.InvariantCulture, $"[{this.Id}]\nvariant = {unpacked.Variant}\npacked_size = {packed.OriginalImage.Length}\nunpacked_size = {unpacked.Executable.Length}\n");
      return new(ExecutableUnpackLevel.RebuiltExecutable, this.Capabilities, [
        new(unpacked.FileName, unpacked.Executable),
        new("metadata.ini", Encoding.UTF8.GetBytes(ini.ToString())),
      ], []);
    } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException) {
      return new(ExecutableUnpackLevel.DetectionOnly, this.Capabilities,
        [new("packed_payload.bin", packed.OriginalImage)],
        [new(ExecutableDiagnosticCode.DecompressionFailed, ex.Message, true)]);
    }
  }
}
