#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// MPRESS (MATCODE Software) packed PE/ELF images.
/// </summary>
/// <remarks>
/// <para>
/// MPRESS 2.x rewrites the image into two sections: <c>.MPRESS1</c> carries the packed
/// original address space, <c>.MPRESS2</c> the loader stub plus the rebuilt import table.
/// MPRESS has no published specification, so the layout of <c>.MPRESS1</c> below was
/// recovered from what the loader stub of packed samples does with it:
/// </para>
/// <list type="table">
///   <item><term>+0</term><description>uint16 — unpacked size in 4 KiB pages; equals the section's virtual size</description></item>
///   <item><term>+2</term><description>uint32 — packed size, counted from +6 and including the two parameter bytes</description></item>
///   <item><term>+6</term><description>uint8 — high nibble = <c>pb</c>, low nibble = <c>lp</c></description></item>
///   <item><term>+7</term><description>uint8 — <c>lc</c></description></item>
///   <item><term>+8</term><description>a bare LZMA1 range-coded stream (no properties byte, no size field)</description></item>
/// </list>
/// <para>
/// The stub decodes that stream over the section itself and then walks the result once
/// more to turn the packer's absolute E8/E9 call and jump operands back into
/// displacements. Both steps are reproduced here, so the artifact this handler emits is
/// the original image's address space from the <c>.MPRESS1</c> base onwards — the
/// pre-relocation, pre-import-resolution memory image, not a rebuilt file.
/// </para>
/// <para>
/// MPRESS 1.x uses a much smaller stub and a different, non-LZMA codec; those samples are
/// detected and their payload is carved, but not decompressed.
/// </para>
/// </remarks>
public sealed class MPressExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "mpress";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MPRESS executable packer";

  private static ReadOnlySpan<byte> MPressLiteral => "MPRESS"u8;
  private static ReadOnlySpan<byte> MatcodeLiteral => "MATCODE"u8;

  /// <summary>Page count, packed size and the two LZMA parameter bytes.</summary>
  private const int HeaderSize = 8;

  /// <summary>The two parameter bytes are counted as part of the packed size.</summary>
  private const int ParameterSize = 2;

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsX86 |
    ExecutableUnpackCapabilities.SupportsX64;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var isPe = PackerScanner.IsPe(image);
    var isElf = image.Length >= 4 && image[0] == 0x7F && image[1] == (byte)'E' && image[2] == (byte)'L' && image[3] == (byte)'F';
    if (!isPe && !isElf)
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "MPRESS: not a PE or ELF executable.", true)]);

    var hasPeSection = isPe && PackerScanner.GetPeSections(image)
      .Any(s => s.Name.StartsWith(".MPRESS", StringComparison.OrdinalIgnoreCase));
    var hasLiteral = PackerScanner.IndexOfBounded(image, MPressLiteral, 0x10000) >= 0 ||
      PackerScanner.IndexOfBounded(image, MatcodeLiteral, 0x10000) >= 0;

    if (hasPeSection || hasLiteral)
      return new(true, this.Id, hasPeSection && hasLiteral ? 1.0 : 0.85, []);

    return new(false, this.Id, 0, [
      new(ExecutableDiagnosticCode.NotPackedExecutable, "MPRESS: no .MPRESS section or MPRESS/MATCODE literal was found.", true),
    ]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      info,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = "MPRESS",
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    if (packed.OriginalImage.LongLength > options.MaximumInputSize)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, "Input exceeds configured executable unpacking size limit.", true),
      ]);

    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var payloads = LocatePayloads(packed.OriginalImage);
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect;

    if (payloads.Count == 1) {
      artifacts.Add(new("compressed_payload.bin", payloads[0].Data, "mpress"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
    } else if (payloads.Count > 1) {
      for (var i = 0; i < payloads.Count; i++)
        artifacts.Add(new($"payload_candidates/candidate_{i:000}_{Sanitize(payloads[i].Name)}.bin", payloads[i].Data, "mpress"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
    } else {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "MPRESS was detected, but no packed section payload could be carved.", true));
    }

    if (level == ExecutableUnpackLevel.PayloadLocated) {
      var container = payloads.FirstOrDefault(p => p.Name.Equals(".MPRESS1", StringComparison.OrdinalIgnoreCase));
      if (container.Data is null)
        diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
          "MPRESS sections were carved, but none of them is the .MPRESS1 packed container.", true));
      else if (!TryReadHeader(container, out var header))
        diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedPackerVersion,
          "MPRESS .MPRESS1 payload located, but its header does not describe an LZMA stream. " +
          "MPRESS 1.x packs with a different codec that this handler cannot decode.", true));
      else if (header.UnpackedSize > options.MaximumDecompressedSize)
        diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
          "MPRESS payload exceeds the configured decompressed size limit.", true));
      else
        try {
          var unpacked = Decompress(container.Data, header);
          UndoCallTransform(unpacked);
          artifacts.Add(new("unpacked_image.bin", unpacked, "mpress-lzma"));
          level = ExecutableUnpackLevel.PayloadDecompressed;
          caps |= ExecutableUnpackCapabilities.CanDecompressPayload;
        } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException or IndexOutOfRangeException) {
          diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
            $"MPRESS .MPRESS1 payload located, but its LZMA stream did not decode: {e.Message}", true));
        }
    }

    if (level == ExecutableUnpackLevel.PayloadDecompressed)
      diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed,
        "unpacked_image.bin is the original address space from the .MPRESS1 base onwards. " +
        "Rebuilding a runnable file from it still needs the import table and the section layout " +
        "the .MPRESS2 loader holds.", false));

    caps |= packed.ImageInfo?.Container switch {
      ExecutableContainerKind.Pe => ExecutableUnpackCapabilities.SupportsPe,
      ExecutableContainerKind.Elf => ExecutableUnpackCapabilities.SupportsElf,
      _ => ExecutableUnpackCapabilities.None,
    };
    caps |= packed.ImageInfo?.Architecture switch {
      CpuArchitecture.X86 => ExecutableUnpackCapabilities.SupportsX86,
      CpuArchitecture.X64 => ExecutableUnpackCapabilities.SupportsX64,
      _ => ExecutableUnpackCapabilities.None,
    };

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private readonly record struct Payload(string Name, byte[] Data, uint VirtualSize);

  /// <summary>Coding parameters and stream bounds read from the .MPRESS1 header.</summary>
  private readonly record struct MPress1Header(int UnpackedSize, int StreamOffset, int StreamLength, int Lc, int Lp, int Pb);

  private static List<Payload> LocatePayloads(byte[] image) {
    var payloads = new List<Payload>();
    if (PackerScanner.IsPe(image)) {
      foreach (var s in PackerScanner.GetPeSectionRanges(image)) {
        if (!s.Name.StartsWith(".MPRESS", StringComparison.OrdinalIgnoreCase))
          continue;
        if (s.RawSize <= 0 || s.RawOffset >= image.Length)
          continue;
        var length = (int)Math.Min(s.RawSize, (uint)(image.Length - s.RawOffset));
        payloads.Add(new(s.Name, image.AsSpan((int)s.RawOffset, length).ToArray(), s.VirtualSize));
      }
    }
    return payloads;
  }

  /// <summary>
  /// Reads the .MPRESS1 header and rejects anything the loader stub would not accept:
  /// a page count that disagrees with the section's virtual size, a packed size that does
  /// not fit the section, or lc/lp/pb outside the ranges LZMA defines.
  /// </summary>
  private static bool TryReadHeader(Payload container, out MPress1Header header) {
    header = default;
    var data = container.Data;
    if (data.Length < HeaderSize)
      return false;

    var unpackedPages = BinaryPrimitives.ReadUInt16LittleEndian(data);
    var packedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(2));
    var unpackedSize = (long)unpackedPages << 12;
    if (unpackedSize == 0 || unpackedSize != RoundUpToPage(container.VirtualSize))
      return false;

    // The packed size counts from the parameter bytes, and the loader reads the stream out
    // of the mapped section — so it may run into the section's zero-filled virtual tail.
    if (packedSize <= ParameterSize || packedSize > container.VirtualSize)
      return false;

    int lp = data[6] & 0x0F, pb = data[6] >> 4, lc = data[7];
    if (lc > 8 || lp > 4 || pb > 4)
      return false;

    var available = data.Length - HeaderSize;
    var wanted = (int)packedSize - ParameterSize;
    header = new((int)unpackedSize, HeaderSize, Math.Min(wanted, available), lc, lp, pb);
    return header.StreamLength > 0;
  }

  private static byte[] Decompress(byte[] container, MPress1Header header) => LzmaBuildingBlock.DecompressRaw(
    container.AsSpan(header.StreamOffset, header.StreamLength),
    header.Lc,
    header.Lp,
    header.Pb,
    header.UnpackedSize);

  /// <summary>
  /// Reverses MPRESS's x86 call/jump transform in place.
  /// </summary>
  /// <remarks>
  /// The packer replaces the displacement of the <c>E8</c>/<c>E9</c> instructions it finds
  /// with the address they point at, so that repeated calls to one target encode
  /// identically and compress better. The loader stub undoes this by scanning the
  /// decompressed image up to <c>size - 0x1000</c>: for every opcode byte that masks down
  /// to <c>E8</c> it reads the little-endian dword <c>v</c> that follows at offset
  /// <c>q</c>, where a value below the scan limit is a converted address and becomes
  /// <c>v - q</c>, a negative value that <c>q</c> lifts back to non-negative is a
  /// displacement the packer biased and becomes <c>v + limit</c>, and anything else was
  /// left alone when packing. The scan resumes after the operand, so an operand byte can
  /// never be mistaken for the next opcode. The rule was read off 32-bit loader stubs; the
  /// operands are 32-bit displacements either way, so the same pass runs over 64-bit
  /// images, which no sample seen so far has exercised.
  /// </remarks>
  private static void UndoCallTransform(byte[] image) {
    var limit = image.Length - 0x1000;
    for (var i = 0; i < limit;) {
      if ((image[i] & 0xFE) != 0xE8) {
        ++i;
        continue;
      }

      var operand = i + 1;
      var value = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(operand));
      if (value < 0x80000000) {
        if (value < (uint)limit)
          BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(operand), value - (uint)operand);
      } else if ((int)value + operand >= 0)
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(operand), value + (uint)limit);

      i = operand + 4;
    }
  }

  private static long RoundUpToPage(uint value) => (value + 0xFFFL) & ~0xFFFL;

  private static string Sanitize(string value) {
    var sb = new StringBuilder(value.Length);
    foreach (var c in value)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.Length == 0 ? "payload" : sb.ToString();
  }

  private static byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"mpress\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"compressionCore\": \"lzma+e8e9\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
