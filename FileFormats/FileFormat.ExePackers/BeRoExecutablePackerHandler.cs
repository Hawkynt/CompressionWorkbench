#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Dictionary.Aplib;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Static unpacker for BeRoEXEPacker (Benjamin Rosseaux, "bero^fr") — a Win32
/// PE packer that replaces the original image with two sections: a BSS-style
/// <c>packerBY</c> section covering the whole original image body, and a
/// <c>bero^fr</c> section holding the loader stub plus the compressed body.
/// Resources stay in a regenerated <c>.rsrc</c>.
/// </summary>
/// <remarks>
/// <para>
/// The container is not documented by the author, so the layout below was
/// derived from the packed images themselves: the entry-point stub is a fixed
/// code blob with patched immediates, and every field this handler needs is one
/// of those immediates. The stub was read instruction by instruction and its
/// behaviour re-implemented; no packer or decompressor source was consulted or
/// copied.
/// </para>
/// <para>
/// Two stub shapes exist, distinguished by the first bytes at the entry point:
/// </para>
/// <list type="bullet">
///   <item><description>
///     LZMA: <c>60</c> (pushad) followed by three <c>68 imm32</c> pushes —
///     compressed size, destination VA, source VA — and a <c>E8 rel32</c> call
///     into the decompressor. The source points at a 13-byte header
///     (<c>props, dictionarySize:u32, uncompressedSize:u32, uncompressedSize:u32</c>;
///     the stub reads the size dword at +5 and then skips 8, so the second copy
///     is never used) followed by a raw LZMA1 stream. The property byte is split
///     by the stub exactly the way LZMA encodes it — <c>pb = p / 45</c>,
///     <c>lp = (p % 45) / 9</c>, <c>lc = p % 9</c> — and the decoder is the
///     standard LZMA1 one (11-bit probabilities, 5-bit adaptation shift, the
///     0/0xC0/0xCC/0xD8/0xE4/0xF0/0x1B0/0x2AF/0x322/0x332/0x534/0x736 probability
///     layout and the 12-state machine).
///   </description></item>
///   <item><description>
///     aPLib: <c>60 BE src:u32 BF dst:u32 FC B2 80 33 DB A4</c> — the classic
///     aPLib byte-oriented depacker (tag bit stream, gamma-coded lengths, the
///     0x7D00/0x500/0x80 offset thresholds), decoding from <c>src</c> to
///     <c>dst</c>.
///   </description></item>
/// </list>
/// <para>
/// Both shapes then run the same E8/E9 call unfilter over a sub-range of the
/// decompressed body, and both parameters come from the stub:
/// <c>FC BE start:u32 B9 bias:u32 2B CE 81 FE end:u32</c>. Scanning forward from
/// <c>start</c> to <c>end</c>, every <c>E8</c>/<c>E9</c> and every two-byte
/// <c>0F 80..0F 8F</c> is followed by a dword from which the stub subtracts the
/// buffer offset of the byte after that dword; the four dword bytes are then
/// skipped, so the packer's forward pass and this reverse pass visit exactly the
/// same positions. The default <c>bias</c> immediate is 4 (the dword width), but
/// samples with a filter range that does not start at the destination carry a
/// different value, which is why it is read rather than assumed.
/// </para>
/// <para>
/// The recovered payload is the original image body as it is mapped at the
/// destination RVA, i.e. every original section's bytes at its original virtual
/// address. It is not a byte-identical copy of the pre-pack file: BeRoEXEPacker
/// regenerates the PE headers, rebuilds the resource section and zeroes the
/// import thunks it resolves itself, so the file-level container is gone and
/// cannot be re-derived from the packed image. The original entry-point RVA and
/// the original import-descriptor RVA <i>are</i> recoverable — the stub's
/// trailing <c>61 E9 rel32</c> jump and its <c>BA imagebase 8D B2 rva</c> import
/// walk hold them — and are reported in <c>metadata.json</c>.
/// </para>
/// <para>
/// Verified against the 130 BeRoEXEPacker samples of the chesvectain/PackingData
/// corpus, for which the pre-pack originals are known: 129 carry the LZMA stub
/// and 1 the aPLib stub, all 130 decode, and the recovered body matches the
/// original sections' bytes at their virtual addresses (the recovered original
/// entry-point RVA equals the original file's <c>AddressOfEntryPoint</c> in
/// every sample; the residual byte differences are the resource section the
/// packer rebuilt and the import thunks it zeroed).
/// </para>
/// <para>
/// If the stub does not match either shape the handler falls back to the generic
/// section-probing path of <see cref="MinorExecutablePackerHandlerBase"/> rather
/// than failing outright.
/// </para>
/// </remarks>
public sealed class BeRoExecutablePackerHandler : MinorExecutablePackerHandlerBase {
    /// <summary>
  /// Gets the id.
  /// </summary>
public override string Id => "beroexepacker";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public override string DisplayName => "BeRoEXEPacker";

    /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
protected override bool IsPackerSection(string name) =>
    name.Contains("bero", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("gu_idata", StringComparison.Ordinal) ||
    name.Equals("gu_rsrc", StringComparison.Ordinal);

    /// <summary>
  /// Gets the literal signature.
  /// </summary>
protected override ReadOnlySpan<byte> LiteralSignature => "BeRo"u8;

    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public override ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  /// <summary>How far past the entry point the stub's fixed code blob reaches.</summary>
  private const int StubScanLength = 0x600;

  private const int LzmaHeaderSize = 13;

  private enum BeRoCodec {
    Lzma,
    Aplib,
  }

  private readonly record struct BeRoStub(
    BeRoCodec Codec,
    uint SourceRva,
    uint DestinationRva,
    uint CompressedSize,
    uint FilterStartRva,
    uint FilterEndRva,
    uint FilterBias,
    uint OriginalEntryPointRva,
    uint ImportDescriptorRva);

    /// <summary>
  /// Performs the unpack operation.
  /// </summary>
public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) =>
    TryUnpackStub(packed, options, out var result)
      ? result
      : base.Unpack(packed, options);

  private bool TryUnpackStub(PackedExecutable packed, UnpackOptions options, out UnpackResult result) {
    result = null!;
    var image = packed.OriginalImage;
    if (packed.ImageInfo is not { Container: ExecutableContainerKind.Pe } info)
      return false;
    if (!TryParseStub(image, info, out var stub))
      return false;
    if (!TryGetFileOffset(info, stub.SourceRva, out var sourceOffset))
      return false;

    var diagnostics = new List<ExecutableDiagnostic>();
    var maximumOutput = (int)Math.Min(options.MaximumDecompressedSize, int.MaxValue);
    byte[] payload;
    try {
      payload = stub.Codec == BeRoCodec.Lzma
        ? DecodeLzma(image, sourceOffset, stub, maximumOutput)
        : DecodeAplib(image, sourceOffset, info, stub, maximumOutput);
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException) {
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
        $"BeRoEXEPacker: the {(stub.Codec == BeRoCodec.Lzma ? "LZMA" : "aPLib")} stream referenced by the stub failed to decode: {ex.Message}", true));
      return false;
    }

    if (payload.Length == 0)
      return false;

    ApplyCallUnfilter(
      payload,
      (long)stub.FilterStartRva - stub.DestinationRva,
      (long)stub.FilterEndRva - stub.DestinationRva,
      (long)stub.DestinationRva + stub.FilterBias - stub.FilterStartRva);

    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildStubMetadataJson(packed, stub, payload.Length), "stored"),
      new("original_packed.bin", image, "stored"),
      new("decompressed_payload.bin", payload, "stored"),
    };
    var caps =
      ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.CanDecompressPayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (info.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;
    var level = ExecutableUnpackLevel.PayloadDecompressed;

    try {
      artifacts.Add(new("reconstructed/reconstructed.exe", PeRebuilder.RebuildSynthetic(info, payload), "stored"));
      level = ExecutableUnpackLevel.RebuiltExecutable;
      caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
      diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        $"BeRoEXEPacker: the rebuilt PE wraps the recovered image body (mapped at RVA 0x{stub.DestinationRva:X}), not a byte-identical " +
        $"re-serialization of the pre-pack file — the packer regenerated the headers and the resource section. The recovered original " +
        $"entry-point RVA (0x{stub.OriginalEntryPointRva:X}) and import-descriptor RVA (0x{stub.ImportDescriptorRva:X}) are reported in metadata.json."));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) {
      diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed, $"BeRoEXEPacker: PE reconstruction failed: {ex.Message}", options.StrictRebuild));
    }

    var built = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, built), "stored"));
    result = built with { Artifacts = artifacts };
    return true;
  }

  private static byte[] DecodeLzma(byte[] image, int sourceOffset, BeRoStub stub, int maximumOutput) {
    if (sourceOffset + LzmaHeaderSize >= image.Length)
      throw new InvalidDataException("the LZMA header runs past the end of the file");

    var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sourceOffset + 5, 4));
    if (uncompressedSize == 0 || uncompressedSize > (uint)maximumOutput)
      throw new InvalidDataException($"implausible uncompressed size {uncompressedSize}");

    // The stub feeds the decoder from the header onwards without bounding the
    // input, so a compressed size that overshoots the file is clamped here.
    var available = image.Length - sourceOffset - LzmaHeaderSize;
    var compressedSize = stub.CompressedSize == 0 ? available : (int)Math.Min(stub.CompressedSize, (uint)available);

    // Distances can never reach further back than the output produced so far, so
    // a window larger than the output is wasted memory — the packer always writes
    // the encoder's nominal dictionary size, which is far bigger than most images.
    var properties = image.AsSpan(sourceOffset, 5).ToArray();
    var window = (uint)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(1)), uncompressedSize);
    BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(1), Math.Max(window, 4096));

    using var input = new MemoryStream(image, sourceOffset + LzmaHeaderSize, compressedSize, writable: false);
    return new LzmaDecoder(input, properties, uncompressedSize).Decode();
  }

  private static byte[] DecodeAplib(byte[] image, int sourceOffset, ExecutableImageInfo info, BeRoStub stub, int maximumOutput) {
    // The aPLib stub carries no size field; the destination region's virtual size
    // is the bound the packer itself sized the image body to.
    var destination = info.Regions.FirstOrDefault(r => r.VirtualAddress == stub.DestinationRva);
    var bound = destination is { VirtualSize: > 0 }
      ? (int)Math.Min(destination.VirtualSize, (ulong)maximumOutput)
      : maximumOutput;
    return AplibBuildingBlock.DecompressRaw(image.AsSpan(sourceOffset), bound, out _, out _);
  }

  /// <summary>
  /// Replays the stub's E8/E9 call unfilter: every call/jump/two-byte-jcc
  /// displacement in <c>[start, end]</c> is turned from the packer's
  /// buffer-absolute form back into a relative one.
  /// </summary>
  private static void ApplyCallUnfilter(byte[] payload, long start, long end, long bias) {
    if (start < 0) start = 0;
    if (end > payload.Length) end = payload.Length;

    for (var i = start; i <= end && i + 1 < payload.Length;) {
      long displacement;
      if (payload[i] is 0xE8 or 0xE9)
        displacement = i + 1;
      else if (payload[i] == 0x0F && (payload[i + 1] & 0xF0) == 0x80)
        displacement = i + 2;
      else {
        ++i;
        continue;
      }

      if (displacement + 4 > payload.Length)
        break;

      var span = payload.AsSpan((int)displacement, 4);
      BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)(BinaryPrimitives.ReadUInt32LittleEndian(span) - (uint)(displacement + bias)));
      i = displacement + 4;
    }
  }

  private static bool TryParseStub(byte[] image, ExecutableImageInfo info, out BeRoStub stub) {
    stub = default;
    if (info.EntryPoint == 0 || !TryGetFileOffset(info, (uint)info.EntryPoint, out var entry))
      return false;
    var length = Math.Min(StubScanLength, image.Length - entry);
    if (length < 0x40 || image[entry] != 0x60)
      return false;
    var window = image.AsSpan(entry, length);

    BeRoCodec codec;
    uint compressedSize = 0, destination, source;
    if (window[1] == 0x68 && window[6] == 0x68 && window[11] == 0x68 && window[16] == 0xE8) {
      // pushad; push compressedSize; push destination; push source; call depacker
      codec = BeRoCodec.Lzma;
      compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(window[2..]);
      destination = BinaryPrimitives.ReadUInt32LittleEndian(window[7..]);
      source = BinaryPrimitives.ReadUInt32LittleEndian(window[12..]);
    } else if (window[1] == 0xBE && window[6] == 0xBF && window[11] == 0xFC && window[12] == 0xB2 && window[13] == 0x80) {
      // pushad; mov esi, source; mov edi, destination; cld; mov dl, 0x80 …
      codec = BeRoCodec.Aplib;
      source = BinaryPrimitives.ReadUInt32LittleEndian(window[2..]);
      destination = BinaryPrimitives.ReadUInt32LittleEndian(window[7..]);
    } else
      return false;

    var imageBase = (uint)info.PreferredBaseAddress;
    if (source <= imageBase || destination <= imageBase)
      return false;

    if (!TryFindCallUnfilterSetup(window, out var filterStart, out var filterBias, out var filterEnd))
      return false;
    if (filterStart < destination || filterEnd < filterStart)
      return false;

    var entryRva = (uint)info.EntryPoint;
    stub = new(
      codec,
      source - imageBase,
      destination - imageBase,
      compressedSize,
      filterStart - imageBase,
      filterEnd - imageBase,
      filterBias,
      FindOriginalEntryPoint(window, entryRva),
      FindImportDescriptorRva(window, imageBase));
    return true;
  }

  /// <summary>
  /// Finds <c>FC BE start:u32 B9 bias:u32 2B CE 81 FE end:u32</c> — the stub's
  /// call-unfilter set-up, which sits right after the depack call in the LZMA
  /// stub and after the depack loop in the aPLib one.
  /// </summary>
  private static bool TryFindCallUnfilterSetup(ReadOnlySpan<byte> window, out uint start, out uint bias, out uint end) {
    start = bias = end = 0;
    for (var i = 0; i + 21 <= window.Length; ++i) {
      if (window[i] != 0xFC || window[i + 1] != 0xBE || window[i + 6] != 0xB9)
        continue;
      if (window[i + 11] != 0x2B || window[i + 12] != 0xCE || window[i + 13] != 0x81 || window[i + 14] != 0xFE)
        continue;
      start = BinaryPrimitives.ReadUInt32LittleEndian(window[(i + 2)..]);
      bias = BinaryPrimitives.ReadUInt32LittleEndian(window[(i + 7)..]);
      end = BinaryPrimitives.ReadUInt32LittleEndian(window[(i + 15)..]);
      return true;
    }
    return false;
  }

  /// <summary>Reads the stub's trailing <c>popad; jmp originalEntryPoint</c>.</summary>
  private static uint FindOriginalEntryPoint(ReadOnlySpan<byte> window, uint entryRva) {
    for (var i = 0; i + 6 <= window.Length; ++i) {
      if (window[i] != 0x61 || window[i + 1] != 0xE9)
        continue;
      // The jump is relative to the instruction after it, and window index 0 is
      // the packed entry point.
      var relative = BinaryPrimitives.ReadInt32LittleEndian(window[(i + 2)..]);
      return (uint)(entryRva + i + 6 + relative);
    }
    return 0;
  }

  /// <summary>
  /// Reads the <c>mov edx, imageBase; lea esi, [edx + importDescriptorRva]</c>
  /// pair that seeds the stub's import-descriptor walk.
  /// </summary>
  private static uint FindImportDescriptorRva(ReadOnlySpan<byte> window, uint imageBase) {
    for (var i = 0; i + 11 <= window.Length; ++i) {
      if (window[i] != 0xBA || window[i + 5] != 0x8D || window[i + 6] != 0xB2)
        continue;
      if (BinaryPrimitives.ReadUInt32LittleEndian(window[(i + 1)..]) != imageBase)
        continue;
      return BinaryPrimitives.ReadUInt32LittleEndian(window[(i + 7)..]);
    }
    return 0;
  }

  private static bool TryGetFileOffset(ExecutableImageInfo info, uint rva, out int offset) {
    offset = 0;
    foreach (var region in info.Regions) {
      var span = Math.Max(region.VirtualSize, region.FileSize);
      if (rva < region.VirtualAddress || rva >= region.VirtualAddress + span)
        continue;
      var delta = rva - region.VirtualAddress;
      if (delta >= region.FileSize)
        return false;
      var candidate = region.FileOffset + delta;
      if (candidate > int.MaxValue)
        return false;
      offset = (int)candidate;
      return true;
    }
    return false;
  }

  private byte[] BuildStubMetadataJson(PackedExecutable packed, BeRoStub stub, int payloadLength) {
    var entryRva = (uint)(packed.ImageInfo?.EntryPoint ?? 0);
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"packer\": \"{this.Id}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"compressionMethod\": \"{(stub.Codec == BeRoCodec.Lzma ? "lzma" : "aplib")}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"payloadRva\": {stub.DestinationRva},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"payloadSize\": {payloadLength},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"originalEntryPointRva\": {stub.OriginalEntryPointRva},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"originalImportDescriptorRva\": {stub.ImportDescriptorRva},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
