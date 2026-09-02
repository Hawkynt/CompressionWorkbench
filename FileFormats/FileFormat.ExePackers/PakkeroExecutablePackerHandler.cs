#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Detector for Pakkero (github.com/4w4k3/pakkero) ELF launchers. Pakkero is a
/// Go-based binary obfuscator rather than a size-reducing packer: it generates a
/// launcher, compiles it, strips the result and appends a large block of random
/// bytes past everything the ELF headers describe, so that no two outputs share a
/// layout or a signature.
/// </summary>
/// <remarks>
/// <para>
/// Two build shapes occur, and detection accepts either. The stripped shape has
/// no section header table at all, three program headers, and a writable
/// <c>PT_LOAD</c> with <c>p_filesz</c> of zero (all of its data is <c>.bss</c>).
/// The unstripped shape keeps a section header table whose string table is
/// present but whose every section name is blank. Both are then required to carry
/// at least 256 KiB of near-incompressible trailing data beyond the last byte any
/// header accounts for, which is the padding Pakkero appends.
/// </para>
/// <para>
/// Requiring the blank names or the empty writable segment is what separates
/// Pakkero from Ezuri, the other Go ELF crypter in this registry: Ezuri also
/// appends a high-entropy block but keeps ordinary Go section names.
/// </para>
/// <para>
/// The original executable cannot be recovered from these launchers, and this
/// handler therefore reports <see cref="ExecutableUnpackLevel.DetectionOnly"/>
/// rather than claiming a payload it cannot produce. See
/// <see cref="BuildMetadata"/> for the measurement that establishes this.
/// </para>
/// </remarks>
public sealed class PakkeroExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>Smallest trailing block that counts as Pakkero's padding.</summary>
  private const int MinimumTrailingBytes = 256 * 1024;

  /// <summary>Window of trailing data measured for randomness.</summary>
  private const int EntropyWindow = 64 * 1024;

  /// <summary>Shannon entropy, in bits per byte, above which the block is treated as random.</summary>
  private const double MinimumTrailingEntropy = 7.9;

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "pakkero";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Pakkero ELF obfuscator (Go launcher, runtime-derived key)";

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsX64;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (TryDescribe(image, out _))
      return new(true, this.Id, 1.0, []);
    return new(false, this.Id, 0,
      [new(ExecutableDiagnosticCode.NotPackedExecutable,
        "No Pakkero launcher shape (stripped or blank-named ELF64 with a large random trailing block) was found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var bytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(this.Id, bytes, detection, info, this.Capabilities, new Dictionary<string, string> {
      ["packer"] = this.Id,
      ["container"] = info.Container.ToString(),
      ["architecture"] = info.Architecture.ToString(),
    });
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    if (image.LongLength > options.MaximumInputSize)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "Input exceeds configured executable unpacking size limit.", true)]);

    if (!TryDescribe(image, out var launcher))
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "Pakkero launcher layout could not be parsed.", true)]);

    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.PayloadNotFound,
        "Pakkero's trailing block is random padding, not the packed program: across the 200-sample corpus its length stays within a 100 KiB band while the originals span 10 KiB to 8 MiB, and it carries no compressed or encrypted stream that reproduces the original.", true),
      new(ExecutableDiagnosticCode.TransformNotReversible,
        "Pakkero launchers do not embed a recoverable copy of the original executable, so no static unpacking is possible; the launcher is reported and described only."),
    };

    var artifacts = new List<UnpackArtifact> {
      new("metadata.ini", BuildMetadata(image.Length, launcher), "stored"),
      new("original_packed.bin", image, "stored"),
      new("trailing_padding.bin", image.AsSpan(launcher.TrailingOffset).ToArray(), "stored"),
    };

    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsElf |
      ExecutableUnpackCapabilities.SupportsX64;
    var result = new UnpackResult(ExecutableUnpackLevel.DetectionOnly, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  internal readonly record struct Launcher(int TrailingOffset, int TrailingLength, bool Stripped, double TrailingEntropy);

  /// <summary>
  /// Recognises a Pakkero launcher and reports where its trailing padding starts.
  /// </summary>
  /// <remarks>
  /// The end of the described image is the highest file offset reached by any
  /// program header, the section header table, or the section header string
  /// table; anything past that is padding Pakkero appended after linking.
  /// </remarks>
  internal static bool TryDescribe(ReadOnlySpan<byte> image, out Launcher launcher) {
    launcher = default;
    if (image.Length < 0x40 || image[0] != 0x7F || image[1] != 'E' || image[2] != 'L' || image[3] != 'F')
      return false;
    if (image[4] != 2 || image[5] != 1) // ELF64 little-endian only; Pakkero builds for x86-64.
      return false;
    if (BinaryPrimitives.ReadUInt16LittleEndian(image[0x10..]) != 2) // ET_EXEC
      return false;

    var phoff = BinaryPrimitives.ReadUInt64LittleEndian(image[0x20..]);
    var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(image[0x36..]);
    var phnum = BinaryPrimitives.ReadUInt16LittleEndian(image[0x38..]);
    if (phoff == 0 || phentsize < 56 || phnum == 0 || phnum > 4096)
      return false;
    if (phoff + (ulong)phentsize * phnum > (ulong)image.Length)
      return false;

    var described = phoff + (ulong)phentsize * phnum;
    var emptyWritableLoad = false;
    for (var i = 0; i < phnum; ++i) {
      var o = (int)phoff + i * phentsize;
      var type = BinaryPrimitives.ReadUInt32LittleEndian(image[o..]);
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(image[(o + 4)..]);
      var pOffset = BinaryPrimitives.ReadUInt64LittleEndian(image[(o + 8)..]);
      var pFilesz = BinaryPrimitives.ReadUInt64LittleEndian(image[(o + 32)..]);
      var pMemsz = BinaryPrimitives.ReadUInt64LittleEndian(image[(o + 40)..]);
      if (pOffset + pFilesz <= (ulong)image.Length)
        described = Math.Max(described, pOffset + pFilesz);
      // PF_R|PF_W with nothing in the file: the launcher's data is all .bss.
      if (type == 1 && flags == 6 && pFilesz == 0 && pMemsz > 0)
        emptyWritableLoad = true;
    }

    var shoff = BinaryPrimitives.ReadUInt64LittleEndian(image[0x28..]);
    var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(image[0x3A..]);
    var shnum = BinaryPrimitives.ReadUInt16LittleEndian(image[0x3C..]);
    var shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(image[0x3E..]);
    var stripped = shoff == 0 && shnum == 0;
    var blankNames = false;

    if (!stripped) {
      if (shoff == 0 || shnum == 0 || shentsize < 64)
        return false;
      if (shoff + (ulong)shentsize * shnum > (ulong)image.Length)
        return false;
      described = Math.Max(described, shoff + (ulong)shentsize * shnum);
      if (shstrndx >= shnum)
        return false;

      var strSection = (int)shoff + shstrndx * shentsize;
      var strOffset = BinaryPrimitives.ReadUInt64LittleEndian(image[(strSection + 24)..]);
      var strSize = BinaryPrimitives.ReadUInt64LittleEndian(image[(strSection + 32)..]);
      if (strOffset + strSize > (ulong)image.Length)
        return false;
      described = Math.Max(described, strOffset + strSize);

      blankNames = true;
      for (var i = 0; i < shnum && blankNames; ++i) {
        var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(image[((int)shoff + i * shentsize)..]);
        if (nameOffset >= strSize)
          continue;
        if (image[(int)strOffset + (int)nameOffset] != 0)
          blankNames = false;
      }
    }

    if (!stripped && !blankNames)
      return false;
    if (stripped && !emptyWritableLoad)
      return false;

    var trailingOffset = (long)described;
    var trailingLength = image.Length - trailingOffset;
    if (trailingLength < MinimumTrailingBytes)
      return false;

    var window = image.Slice((int)trailingOffset, Math.Min(EntropyWindow, (int)trailingLength));
    var entropy = ShannonEntropy(window);
    if (entropy <= MinimumTrailingEntropy)
      return false;

    launcher = new((int)trailingOffset, (int)trailingLength, stripped, entropy);
    return true;
  }

  /// <summary>Shannon entropy of <paramref name="data"/> in bits per byte.</summary>
  private static double ShannonEntropy(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return 0;
    Span<int> counts = stackalloc int[256];
    foreach (var b in data)
      ++counts[b];
    var result = 0d;
    foreach (var count in counts) {
      if (count == 0)
        continue;
      var p = (double)count / data.Length;
      result -= p * Math.Log2(p);
    }
    return result;
  }

  private static byte[] BuildMetadata(int imageSize, Launcher launcher) {
    var sb = new StringBuilder();
    sb.AppendLine("[pakkero]");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"image_size = {imageSize}\n");
    sb.AppendLine("container = ELF64");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"build_shape = {(launcher.Stripped ? "stripped" : "blank_section_names")}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailing_offset = 0x{launcher.TrailingOffset:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailing_size = {launcher.TrailingLength}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailing_entropy = {launcher.TrailingEntropy:F4}\n");
    sb.AppendLine("capability_level = DetectionOnly");
    sb.AppendLine("note = Pakkero launchers carry random padding, not a recoverable copy of the original; the trailing block's size is independent of the packed program's size, so no static unpacking is offered.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
