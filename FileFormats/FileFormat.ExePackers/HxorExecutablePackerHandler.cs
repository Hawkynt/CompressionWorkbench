#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Static unpacker for hXOR-Packer (github.com/rurararura/hXOR-Packer, Afif
/// 2012) — an educational Win32 EXE packer/binder.
/// </summary>
/// <remarks>
/// <para>
/// Container, appended after the copied unpacker stub:
/// <c>[unpacker stub][4-byte signature]["packdata_t" (272 bytes)][payload]</c>.
/// The packer writes a multi-character literal <c>'AFIF'</c> as a native
/// <c>long</c>; on the little-endian x86 target this lands on disk as the
/// ASCII bytes <c>"FIFA"</c>, which is what a byte scan actually finds. The
/// insert offset (the stub's own file size) is written into the DOS header's
/// reserved <c>e_res2</c> field (offset 0x28) as a little-endian 32-bit value —
/// so locating the payload is O(1), no scanning required.
/// </para>
/// <para>
/// <c>packdata_t</c> is <c>{ char filename[260]; int32 filesize; int32 key;
/// int32 parameter; }</c> (272 bytes, naturally aligned, no padding).
/// <c>parameter</c> selects the transform applied to the payload:
/// 0 = stored, 1 = compressed with the packer's bespoke Huffman coder
/// (<c>packer/src/huffman.cpp</c>), 2 = single-byte XOR-encrypted, 3 = both
/// (Huffman first is undone, then XOR). Only 0 and 2 are statically decoded
/// here — the Huffman variant is not yet replicated, and samples using it are
/// reported at <see cref="ExecutableUnpackLevel.PayloadLocated"/> with a
/// precise diagnostic rather than a fabricated decode.
/// </para>
/// <para>
/// The XOR key (parameter 2) is derived by seeding the classic MSVCRT
/// linear-congruential <c>rand()</c> — <c>state = state * 214013 + 2531011;
/// return (state &gt;&gt; 16) &amp; 0x7FFF</c> — with either the user-supplied key
/// (if the packer was invoked with one) or the original file size, then
/// taking <c>rand() % 69</c> as a single repeating XOR byte over the whole
/// payload (not a rolling keystream: <c>rand()</c> is called exactly once).
/// This LCG was verified bit-for-bit against a MinGW-w64 build's actual
/// <c>rand()</c>/<c>srand()</c> (which thunks to msvcrt.dll) for multiple
/// seeds, so the derivation below is a faithful, fully static reimplementation.
/// </para>
/// </remarks>
public sealed class HxorExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "hxor";
  public string DisplayName => "hXOR-Packer";

  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  private const int MaxPath = 260;
  private const int PackDataSize = MaxPath + 4 + 4 + 4; // filename + filesize + key + parameter
  private const int DosResvOffset = 0x28; // IMAGE_DOS_HEADER.e_res2

  private static ReadOnlySpan<byte> FifaSignature => "FIFA"u8;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!TryGetInsertOffset(image, out var offset))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "hXOR: not a valid MZ image, or e_res2 holds no plausible insert offset.", true)]);

    if (offset + 4 > image.Length || !image.Slice(offset, 4).SequenceEqual(FifaSignature))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "hXOR: \"FIFA\" signature not found at the e_res2 insert offset.", true)]);

    if (offset + 4 + PackDataSize > image.Length)
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "hXOR: packdata_t would extend past EOF.", true)]);

    return new(true, this.Id, 1.0, []);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    var metadata = new Dictionary<string, string> {
      ["packer"] = "hXOR-Packer",
      ["container"] = info.Container.ToString(),
      ["architecture"] = info.Architecture.ToString(),
    };
    if (TryGetInsertOffset(image, out var offset))
      metadata["insertOffset"] = offset.ToString(CultureInfo.InvariantCulture);
    return new(this.Id, imageBytes, detection, info, this.Capabilities, metadata);
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    var diagnostics = new List<ExecutableDiagnostic>();
    var artifacts = new List<UnpackArtifact> { new("original_packed.bin", image, "stored") };

    if (!TryGetInsertOffset(image, out var offset) ||
        offset + 4 + PackDataSize > image.Length ||
        !image.AsSpan(offset, 4).SequenceEqual(FifaSignature)) {
      diagnostics.Add(new(ExecutableDiagnosticCode.NotPackedExecutable, "hXOR: signature/offset validation failed while unpacking.", true));
      var failResult = new UnpackResult(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, artifacts, diagnostics);
      artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, failResult), "stored"));
      return failResult with { Artifacts = artifacts };
    }

    var pdataOffset = offset + 4;
    artifacts.Add(new("unpacker_stub.bin", image.AsSpan(0, offset).ToArray(), "stored"));

    var filename = ReadFixedAscii(image.AsSpan(pdataOffset, MaxPath));
    var filesize = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(pdataOffset + MaxPath, 4));
    var key = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(pdataOffset + MaxPath + 4, 4));
    var parameter = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(pdataOffset + MaxPath + 8, 4));

    var payloadOffset = pdataOffset + PackDataSize;
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;

    if (filesize < 0 || filesize > options.MaximumDecompressedSize || payloadOffset + (long)filesize > image.Length) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "hXOR: packdata_t.filesize extends the payload past EOF or exceeds the configured limit.", true));
      var badResult = new UnpackResult(level, caps, artifacts, diagnostics);
      artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, badResult), "stored"));
      return badResult with { Artifacts = artifacts };
    }

    var payload = image.AsSpan(payloadOffset, filesize).ToArray();
    artifacts.Add(new("payload.bin", payload, "stored"));
    artifacts.Add(new("metadata.json", BuildMetadataJson(filename, filesize, key, parameter), "stored"));
    level = ExecutableUnpackLevel.PayloadLocated;
    caps |= ExecutableUnpackCapabilities.CanLocatePayload;

    byte[]? original = null;
    switch (parameter) {
      case 0: // stored verbatim
        original = payload;
        break;

      case 2: { // single-byte XOR, keyed by a single MSVCRT rand() draw
        var seed = unchecked((uint)(key != 0 ? key : filesize));
        var xorKey = (byte)(MsvcrtRand(seed) % 69);
        var decoded = new byte[payload.Length];
        for (var i = 0; i < payload.Length; i++)
          decoded[i] = (byte)(payload[i] ^ xorKey);
        original = decoded;
        artifacts.Add(new("xor_key.txt", Encoding.ASCII.GetBytes(xorKey.ToString(CultureInfo.InvariantCulture)), "stored"));
        break;
      }

      case 1:
      case 3:
        diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
          "hXOR: payload located, but packdata_t.parameter selects the packer's bespoke Huffman compressor (huffman.cpp), which is not yet statically replicated here; only the stored (0) and XOR-only (2) transforms decode.", true));
        break;

      default:
        diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod, $"hXOR: unrecognized packdata_t.parameter value {parameter}.", true));
        break;
    }

    if (original != null) {
      artifacts.Add(new("decompressed_payload.bin", original, "stored"));
      level = ExecutableUnpackLevel.PayloadDecompressed;
      caps |= ExecutableUnpackCapabilities.CanDecompressPayload;

      if (PackerScanner.IsPe(original)) {
        artifacts.Add(new("reconstructed/reconstructed.exe", original, "stored"));
        level = ExecutableUnpackLevel.RebuiltExecutable;
        caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
      } else
        diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed, "hXOR: decoded payload is not a recognizable MZ/PE image.", true));
    }

    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  /// <summary>
  /// Reimplementation of the classic MSVCRT rand() linear-congruential
  /// generator that hXOR's MinGW build links against: <c>srand(seed)</c> sets
  /// the 32-bit state directly, and <c>rand()</c> advances it once and returns
  /// bits 30..16. Verified bit-for-bit against an actual MinGW-w64 build's
  /// <c>rand()</c>/<c>srand()</c> output for several seeds.
  /// </summary>
  public static int MsvcrtRand(uint seed) {
    var state = unchecked(seed * 214013u + 2531011u);
    return (int)((state >> 16) & 0x7FFF);
  }

  private static bool TryGetInsertOffset(ReadOnlySpan<byte> image, out int offset) {
    offset = 0;
    if (!PackerScanner.IsMzExecutable(image) || image.Length < DosResvOffset + 4)
      return false;
    var raw = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(DosResvOffset, 4));
    if (raw <= 0 || raw >= image.Length)
      return false;
    offset = raw;
    return true;
  }

  private static string ReadFixedAscii(ReadOnlySpan<byte> bytes) {
    var end = bytes.IndexOf((byte)0);
    if (end < 0) end = bytes.Length;
    return Encoding.ASCII.GetString(bytes[..end]);
  }

  private static byte[] BuildMetadataJson(string filename, int filesize, int key, int parameter) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"hxor\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"originalFilename\": \"{filename.Replace("\\", "\\\\", StringComparison.Ordinal)}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"payloadSize\": {filesize},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"key\": {key},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"parameter\": {parameter}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
