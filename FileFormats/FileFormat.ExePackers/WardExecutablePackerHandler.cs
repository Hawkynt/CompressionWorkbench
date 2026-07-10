#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Static unpacker for Ward (github.com/ex0dus-0x/ward) ELF packers. Ward
/// appends the original target ELF verbatim to the end of a clang-built stub
/// and repoints the stub's <c>PT_NOTE</c> program header at it (a classic
/// PT_NOTE infection): <c>p_offset</c> becomes the stub's original file size and
/// <c>p_filesz</c> becomes the original ELF's length. Although Ward's README
/// advertises zlib compression, the current injector discards the compressed
/// buffer and stores the target uncompressed, so the original executable is
/// recovered byte-for-byte by carving the injected note region.
/// </summary>
public sealed class WardExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "ward";
  public string DisplayName => "Ward ELF PT_NOTE packer";

  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsX86 |
    ExecutableUnpackCapabilities.SupportsX64 |
    ExecutableUnpackCapabilities.SupportsArm32 |
    ExecutableUnpackCapabilities.SupportsArm64;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (TryLocatePayload(image.ToArray(), out _, out _))
      return new(true, this.Id, 1.0, []);
    return new(false, this.Id, 0,
      [new(ExecutableDiagnosticCode.NotPackedExecutable,
        "No Ward PT_NOTE segment pointing at an appended ELF payload was found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var bytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(this.Id, bytes, detection, info, this.Capabilities, new Dictionary<string, string> {
      ["packer"] = this.Id,
      ["container"] = info.Container.ToString(),
      ["architecture"] = info.Architecture.ToString(),
    });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    if (image.LongLength > options.MaximumInputSize)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "Input exceeds configured executable unpacking size limit.", true)]);

    if (!TryLocatePayload(image, out var offset, out var length))
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "Ward PT_NOTE payload could not be located.", true)]);

    var payload = image.AsSpan(offset, length).ToArray();
    var artifacts = new List<UnpackArtifact> {
      new("metadata.ini", BuildMetadata(image.Length, offset, length), "stored"),
      new("original_packed.bin", image, "stored"),
      new("reconstructed/original_executable.bin", payload, "stored"),
    };

    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.CanDecompressPayload | ExecutableUnpackCapabilities.CanRebuildExecutable |
      ExecutableUnpackCapabilities.SupportsElf | ExecutableUnpackCapabilities.SupportsX86 |
      ExecutableUnpackCapabilities.SupportsX64 | ExecutableUnpackCapabilities.SupportsArm32 |
      ExecutableUnpackCapabilities.SupportsArm64;

    var result = new UnpackResult(ExecutableUnpackLevel.RebuiltExecutable, caps, artifacts, []);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  /// <summary>
  /// Finds a <c>PT_NOTE</c> program header whose file region begins with an ELF
  /// header and runs to end-of-file — the shape produced by Ward's injector,
  /// which appends the target ELF and points the repurposed note at it. Ordinary
  /// notes (build-id, ABI-tag) never contain a nested ELF image, so this does not
  /// false-match unpacked binaries.
  /// </summary>
  internal static bool TryLocatePayload(byte[] image, out int offset, out int length) {
    offset = 0;
    length = 0;
    if (image.Length < 0x40 || image[0] != 0x7F || image[1] != 'E' || image[2] != 'L' || image[3] != 'F')
      return false;

    var is64 = image[4] == 2;
    var le = image[5] == 1;
    if (!le)
      return false;

    ulong phoff;
    ushort phentsize;
    ushort phnum;
    if (is64) {
      phoff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x20));
      phentsize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x36));
      phnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x38));
    } else {
      phoff = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x1C));
      phentsize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x2A));
      phnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x2C));
    }
    if (phoff == 0 || phentsize < (is64 ? 56 : 32) || phnum == 0 || phnum > 4096)
      return false;
    if (phoff + (ulong)phentsize * phnum > (ulong)image.Length)
      return false;

    for (var i = 0; i < phnum; i++) {
      var o = (int)phoff + i * phentsize;
      var type = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o));
      if (type != 4) // PT_NOTE
        continue;

      ulong pOffset;
      ulong pFilesz;
      if (is64) {
        pOffset = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(o + 8));
        pFilesz = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(o + 32));
      } else {
        pOffset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o + 4));
        pFilesz = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o + 16));
      }

      if (pFilesz < 0x40 || pOffset + pFilesz > (ulong)image.Length)
        continue;
      var start = (int)pOffset;
      if (image[start] != 0x7F || image[start + 1] != 'E' || image[start + 2] != 'L' || image[start + 3] != 'F')
        continue;
      // Ward's payload is appended, so its note region reaches end of file.
      if (pOffset + pFilesz != (ulong)image.Length)
        continue;

      offset = start;
      length = (int)pFilesz;
      return true;
    }
    return false;
  }

  private static byte[] BuildMetadata(int imageSize, int offset, int length) {
    var sb = new StringBuilder();
    sb.AppendLine("[ward]");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"image_size = {imageSize}\n");
    sb.AppendLine("container = ELF");
    sb.AppendLine("method = pt_note_infection");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"payload_offset = 0x{offset:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"payload_size = {length}\n");
    sb.AppendLine("capability_level = RebuiltExecutable");
    sb.AppendLine("note = Ward stores the target ELF uncompressed at end-of-file behind a repurposed PT_NOTE header; the original is recovered verbatim.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
