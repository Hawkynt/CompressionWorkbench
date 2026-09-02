#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Dictionary.Aplib;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Real unpack handler for RLPack (ap0x) — a Win32 PE packer that stores the
/// original image, section by section, in a <c>.RLPack</c> section and inflates it
/// into an adjacent uninitialised <c>.packed</c> section at run time.
/// </summary>
/// <remarks>
/// <para>
/// The layout below was recovered from the behaviour of the loader stub itself, as
/// carried by the packed executables in the public <c>chesvectain/PackingData</c>
/// corpus; RLPack publishes no format specification. Everything the unpacker needs
/// is reachable from the entry point, because the stub is position independent and
/// addresses all of its own data relative to a base it computes at run time:
/// </para>
/// <code>
///   pushad                  ; 60
///   call $+5                ; E8 00 00 00 00
///   mov  ebp,[esp]          ; 8B 2C 24      -> ebp = entryPoint + 6
///   add  esp,4              ; 83 C4 04
///   ...
///   lea  esi,[ebp+imm32]    ; 8D B5 imm32   -> esi = the block table
/// </code>
/// <para>
/// The block table is an array of 8-byte <c>{ sourceRva, destinationRva }</c> pairs
/// terminated by a zero dword. Each entry describes one section of the original
/// file: <c>sourceRva</c> points at a compressed stream inside <c>.RLPack</c> and
/// <c>destinationRva</c> is the RVA the section had in the original image. A block
/// inflates to the section's raw file bytes, padded to the original file alignment,
/// so a decoded block is byte-identical to the corresponding section of the
/// unpacked executable.
/// </para>
/// <para>
/// Two compression cores appear in the wild, selectable when packing. The stub
/// reveals which by the calling convention it uses for the depacker: the LZMA
/// variant passes <c>(destination, source, probabilityArray)</c> and allocates the
/// probability array up front, the aPLib variant passes the classic
/// <c>aP_depack(source, destination)</c> pair. The compressed streams themselves
/// carry no header, so the unpacker simply tries both cores per block.
/// </para>
/// <para>
/// The LZMA streams are bare LZMA1 — no properties byte, no dictionary size, no
/// length field — coded with lc=8, lp=0, pb=2 and terminated by an end-of-stream
/// marker rather than a length. The stub's decoder makes all three visible: it
/// clears 0x30736 probabilities, which is exactly the <c>1846 + (0x300 &lt;&lt; (lc+lp))</c>
/// of lc=8/lp=0; it indexes the literal coder by the whole previous byte; it masks
/// the position with 3; and it returns the output length when the decoded distance
/// comes out as 0xFFFFFFFF.
/// </para>
/// <para>
/// Immediately before the block table the stub keeps the three fields describing its
/// x86 call/jump filter — <c>{ codeRva, codeSize, markerByte }</c> at table-0x1C,
/// table-0x18 and table-0x14 — and applies the filter only when the first two are
/// non-zero. The filter rewrites the operand of every <c>E8</c>/<c>E9</c> whose first
/// operand byte equals the marker: the remaining three bytes are the target's
/// big-endian offset within the filtered region. The marker is chosen per packed
/// file, so it has to be read rather than assumed.
/// </para>
/// <para>
/// What the emitted payload is not: RLPack zeroes the original import thunks and
/// rebuilds them at run time from a name table in the stub, so the sections holding
/// the imports (<c>.rdata</c>, <c>.idata</c>) come back with that area blanked. A
/// natively-runnable rebuild therefore needs the stub's import replay and original
/// entry point on top of the decompression this handler performs, and that is
/// reported as a diagnostic rather than claimed.
/// </para>
/// </remarks>
public sealed class RlPackExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "rlpack";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "RLPack aPLib/LZMA-packed PE";

  private const string PackerLabel = "RLPack";

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, $"{PackerLabel}: not a valid PE.", true)]);

    var hasLiteral = PackerScanner.IndexOfBounded(image, "RLPack"u8, 0x10000) >= 0;
    var hasSection = PackerScanner.GetPeSections(image).Any(s => s.Name.Equals(".RLPack", StringComparison.OrdinalIgnoreCase));
    return hasLiteral || hasSection
      ? new(true, this.Id, hasLiteral && hasSection ? 1.0 : 0.8, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, $"{PackerLabel}: no 'RLPack' literal or .RLPack section found.", true)]);
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
        ["packer"] = PackerLabel,
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

    var image = packed.OriginalImage;
    var diagnostics = new List<ExecutableDiagnostic>();
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", image, "stored"),
    };

    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;

    var layout = RlPackFormat.Locate(image, packed.ImageInfo);
    if (layout is not { } found) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
        $"{PackerLabel} detected but the packed section and its uninitialised destination could not be paired.", true));
      return Finish(this.Id, packed, level, caps, artifacts, diagnostics);
    }

    artifacts.Add(new("compressed_payload.bin", found.PayloadSection, "rlpack"));
    level = ExecutableUnpackLevel.PayloadLocated;
    caps |= ExecutableUnpackCapabilities.CanLocatePayload;

    if (found.TableOffset < 0) {
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedPackerVersion,
        $"{PackerLabel} payload located, but the stub at the entry point does not use the known " +
        "`pushad; call $+5; mov ebp,[esp]` / `lea esi,[ebp+imm32]` prologue, so the block table could not be addressed.", true));
      return Finish(this.Id, packed, level, caps, artifacts, diagnostics);
    }

    var blocks = RlPackFormat.DecodeBlocks(image, found, options.MaximumDecompressedSize, out var failure);
    if (blocks.Count == 0 || failure is not null) {
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
        $"{PackerLabel} block table found at 0x{found.TableOffset:X} but {failure ?? "it described no blocks"}.", true));
      return Finish(this.Id, packed, level, caps, artifacts, diagnostics);
    }

    var filtered = RlPackFormat.ApplyCallFilter(image, found, blocks, diagnostics);

    foreach (var block in blocks) {
      artifacts.Add(new($"blocks/block@0x{block.DestinationRva:X}.{block.Codec}", block.Compressed, block.Codec));
      artifacts.Add(new($"sections/section@0x{block.DestinationRva:X}.bin", block.Data, "stored"));
    }

    if (found.DestinationSize <= options.MaximumDecompressedSize)
      artifacts.Add(new("decompressed_payload.bin", RlPackFormat.AssembleImageRegion(found, blocks), "stored"));
    else
      diagnostics.Add(new(ExecutableDiagnosticCode.MemoryImageBuildFailed,
        $"The destination section spans 0x{found.DestinationSize:X} bytes, past the configured decompressed-size limit; " +
        "the per-section artifacts are emitted but the assembled image region is not."));
    level = ExecutableUnpackLevel.PayloadDecompressed;
    caps |= ExecutableUnpackCapabilities.CanDecompressPayload;

    diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
      $"{PackerLabel} payload decompressed: {blocks.Count} block(s) via " +
      $"{string.Join("/", blocks.Select(b => b.Codec).Distinct())}" +
      (filtered ? ", x86 call/jump filter reversed" : "") +
      ". A natively-runnable PE additionally needs the stub's import replay (RLPack blanks the original " +
      "import thunks) and the original entry point, which are loader-version specific."));

    return Finish(this.Id, packed, level, caps, artifacts, diagnostics);
  }

  private static UnpackResult Finish(
    string id,
    PackedExecutable packed,
    ExecutableUnpackLevel level,
    ExecutableUnpackCapabilities caps,
    List<UnpackArtifact> artifacts,
    List<ExecutableDiagnostic> diagnostics) {
    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"packer\": \"{packed.PackerId}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"compressionCore\": \"lzma|aplib\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}

/// <summary>
/// The RLPack container: locating the stub's block table, inflating the blocks it
/// points at and reversing the x86 call/jump filter the stub applies to the code
/// region. Split out from the handler so the format can be exercised directly.
/// </summary>
/// <remarks>
/// See <see cref="RlPackExecutablePackerHandler"/> for how the layout was recovered
/// and what each field means.
/// </remarks>
internal static class RlPackFormat {
  /// <summary>
  /// LZMA properties byte for lc=8, lp=0, pb=2 — <c>(pb * 5 + lp) * 9 + lc</c>. The
  /// bare streams RLPack stores carry no properties of their own, so the decoder is
  /// driven with the parameters read out of the stub's decompressor.
  /// </summary>
  private const byte LzmaPropertiesByte = (2 * 5 + 0) * 9 + 8;

  /// <summary>Offsets of the call-filter fields relative to the block table.</summary>
  private const int CodeRvaOffset = -0x1C;
  private const int CodeSizeOffset = -0x18;
  private const int MarkerOffset = -0x14;

  /// <summary>Largest plausible block count; the real tables hold one entry per original section.</summary>
  private const int MaximumBlockCount = 96;

  internal sealed record Layout(
    int TableOffset,
    byte[] PayloadSection,
    uint PayloadRva,
    uint PayloadRawOffset,
    uint PayloadRawSize,
    uint DestinationRva,
    uint DestinationSize);

  internal sealed record Block(uint SourceRva, uint DestinationRva, byte[] Compressed, byte[] Data, string Codec);

  /// <summary>
  /// Pairs the initialised payload section with the uninitialised section it inflates
  /// into and resolves the stub's block table address. <see cref="Layout.TableOffset"/>
  /// is negative when the entry point does not carry a stub prologue we recognise.
  /// </summary>
  internal static Layout? Locate(byte[] image, ExecutableImageInfo? info) {
    if (info is not { Container: ExecutableContainerKind.Pe })
      return null;

    // RLPack emits exactly two sections: the packed one carrying stub plus data, and
    // an uninitialised one sized to hold the original image.
    var payload =
      info.Regions.FirstOrDefault(r => r.Name.Equals(".RLPack", StringComparison.OrdinalIgnoreCase) && r.FileSize > 0) ??
      info.Regions.Where(r => r.FileSize > 0).OrderByDescending(r => r.FileSize).FirstOrDefault();
    var destination = info.Regions
      .Where(r => r.FileSize == 0 && r.VirtualSize > 0)
      .OrderByDescending(r => r.VirtualSize)
      .FirstOrDefault();
    if (payload is null || destination is null)
      return null;
    if (payload.FileOffset > (ulong)image.Length || payload.FileSize > (ulong)image.Length - payload.FileOffset)
      return null;
    if (payload.FileSize > int.MaxValue || destination.VirtualSize > int.MaxValue)
      return null;

    var bytes = image.AsSpan((int)payload.FileOffset, (int)payload.FileSize).ToArray();
    return new(
      FindTableOffset(image, info, payload),
      bytes,
      (uint)payload.VirtualAddress,
      (uint)payload.FileOffset,
      (uint)payload.FileSize,
      (uint)destination.VirtualAddress,
      (uint)destination.VirtualSize);
  }

  /// <summary>
  /// Follows the stub's own addressing to the block table: the entry point computes
  /// <c>ebp = entryPoint + 6</c> with a <c>call $+5</c> and then loads the table with
  /// <c>lea esi,[ebp+imm32]</c>. Returns a negative value when that prologue is absent.
  /// </summary>
  private static int FindTableOffset(byte[] image, ExecutableImageInfo info, ExecutableRegion payload) {
    var entryRva = info.EntryPoint;
    if (entryRva < payload.VirtualAddress || entryRva >= payload.VirtualAddress + payload.FileSize)
      return -1;

    var end = (int)(payload.FileOffset + payload.FileSize);
    var entry = (int)(entryRva - payload.VirtualAddress + payload.FileOffset);
    if (entry < 0 || entry + 12 > end)
      return -1;

    var stub = image.AsSpan(entry);
    if (stub[0] != 0x60 || stub[1] != 0xE8 || BinaryPrimitives.ReadUInt32LittleEndian(stub[2..]) != 0 ||
        stub[6] != 0x8B || stub[7] != 0x2C || stub[8] != 0x24)
      return -1;

    var limit = Math.Min(entry + 0x80, end - 6);
    for (var i = entry + 9; i < limit; ++i) {
      if (image[i] != 0x8D || image[i + 1] != 0xB5)
        continue;

      var table = (long)entry + 6 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(i + 2));
      return table >= (long)payload.FileOffset && table + 12 <= end ? (int)table : -1;
    }

    return -1;
  }

  /// <summary>
  /// Reads the zero-terminated <c>{ sourceRva, destinationRva }</c> table and inflates
  /// every block it names, trying the LZMA core first and the aPLib core second.
  /// </summary>
  /// <param name="failure">Why the table was rejected, or <see langword="null"/> on success.</param>
  internal static List<Block> DecodeBlocks(byte[] image, Layout layout, long maximumDecompressedSize, out string? failure) {
    var blocks = new List<Block>();
    failure = null;

    var end = (int)(layout.PayloadRawOffset + layout.PayloadRawSize);
    var payloadEndRva = layout.PayloadRva + layout.PayloadRawSize;
    var destinationEndRva = layout.DestinationRva + layout.DestinationSize;
    uint previousSource = 0, previousDestination = 0;

    for (var cursor = layout.TableOffset; cursor + 8 <= end; cursor += 8) {
      var sourceRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(cursor));
      if (sourceRva == 0)
        break;

      var destinationRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(cursor + 4));
      if (sourceRva < layout.PayloadRva || sourceRva >= payloadEndRva) {
        failure = $"entry {blocks.Count} points outside the packed section";
        return blocks;
      }
      if (destinationRva < layout.DestinationRva || destinationRva >= destinationEndRva) {
        failure = $"entry {blocks.Count} lands outside the destination section";
        return blocks;
      }
      if (blocks.Count > 0 && (sourceRva <= previousSource || destinationRva <= previousDestination)) {
        failure = $"entry {blocks.Count} is not ordered after its predecessor";
        return blocks;
      }
      if (blocks.Count >= MaximumBlockCount) {
        failure = $"the table exceeds {MaximumBlockCount} entries";
        return blocks;
      }

      var offset = (int)(sourceRva - layout.PayloadRva + layout.PayloadRawOffset);
      var maximum = (int)Math.Min(destinationEndRva - destinationRva, (uint)Math.Min(maximumDecompressedSize, int.MaxValue));
      var block = Inflate(image, offset, sourceRva, destinationRva, maximum);
      if (block is null) {
        failure = $"entry {blocks.Count} decoded as neither an LZMA nor an aPLib stream";
        return blocks;
      }

      blocks.Add(block);
      previousSource = sourceRva;
      previousDestination = destinationRva;
    }

    return blocks;
  }

  /// <summary>
  /// Inflates one block. Neither core's stream is self-identifying, so both are tried;
  /// each is self-validating enough to reject the other's data — LZMA by running past
  /// the destination bound or faulting on an impossible distance, aPLib by failing to
  /// reach its end-of-stream marker.
  /// </summary>
  private static Block? Inflate(byte[] image, int offset, uint sourceRva, uint destinationRva, int maximum) {
    if (offset < 0 || offset >= image.Length || maximum <= 0)
      return null;

    try {
      Span<byte> properties = stackalloc byte[5];
      properties[0] = LzmaPropertiesByte;
      BinaryPrimitives.WriteInt32LittleEndian(properties[1..], maximum);

      using var input = new MemoryStream(image, offset, image.Length - offset, writable: false);
      // A trailing byte of headroom on the limit turns "ran to the bound" — which a
      // stream of the wrong codec does — into a rejectable overrun rather than a
      // silent truncation, because a real block stops at its end-of-stream marker.
      var data = new LzmaDecoder(input, properties.ToArray(), maximum + 1L).Decode();
      // Position is relative to the window the stream was opened on, so it already is
      // the number of packed bytes the range decoder consumed.
      if (data.Length > 0 && data.Length <= maximum)
        return new(sourceRva, destinationRva, image.AsSpan(offset, (int)input.Position).ToArray(), data, "lzma");
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or EndOfStreamException or IndexOutOfRangeException) {
      // Not an LZMA stream; fall through to aPLib.
    }

    try {
      var data = AplibBuildingBlock.DecompressRaw(image.AsSpan(offset), maximum, out var endMarkerHit, out var consumed);
      if (endMarkerHit && data.Length > 0 && data.Length <= maximum)
        return new(sourceRva, destinationRva, image.AsSpan(offset, consumed).ToArray(), data, "aplib");
    } catch (InvalidDataException) {
      // Neither core accepts the stream.
    }

    return null;
  }

  /// <summary>
  /// Reverses the stub's x86 call/jump filter over the code block, using the region and
  /// marker byte the stub stores just ahead of the block table. Returns whether the
  /// filter ran; a packed file that did not enable it leaves the fields zeroed.
  /// </summary>
  internal static bool ApplyCallFilter(byte[] image, Layout layout, List<Block> blocks, List<ExecutableDiagnostic> diagnostics) {
    // CodeRvaOffset is the furthest back of the three, so it is the one that has to fit.
    if (layout.TableOffset + CodeRvaOffset < (int)layout.PayloadRawOffset)
      return false;

    var codeRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(layout.TableOffset + CodeRvaOffset));
    var codeSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(layout.TableOffset + CodeSizeOffset));
    var marker = image[layout.TableOffset + MarkerOffset];

    // The stub itself skips the filter unless both region fields are set.
    if (codeRva == 0 || codeSize == 0)
      return false;

    var index = blocks.FindIndex(b => b.DestinationRva == codeRva);
    if (index < 0 || blocks[index].Data.Length != codeSize) {
      diagnostics.Add(new(ExecutableDiagnosticCode.TransformNotReversible,
        $"RLPack names a filtered code region at RVA 0x{codeRva:X} of 0x{codeSize:X} bytes that matches no decoded block; " +
        "the call/jump filter was left in place, so the code block still holds absolute targets."));
      return false;
    }

    if (marker == 0) {
      diagnostics.Add(new(ExecutableDiagnosticCode.TransformNotReversible,
        "RLPack used the marker-less variant of its call/jump filter, which this handler does not reverse; " +
        "the code block still holds absolute targets."));
      return false;
    }

    blocks[index] = blocks[index] with { Data = ReverseCallFilter(blocks[index].Data, marker) };
    return true;
  }

  /// <summary>
  /// Turns the filter's absolute form back into the relative operands the original code
  /// had. A filtered operand is <c>marker</c> followed by the target's 24-bit big-endian
  /// offset within the region; converted sites are skipped whole so an operand byte can
  /// never be mistaken for the next opcode.
  /// </summary>
  internal static byte[] ReverseCallFilter(byte[] data, byte marker) {
    var result = (byte[])data.Clone();
    for (var i = 0; i + 5 <= result.Length; ) {
      if ((result[i] != 0xE8 && result[i] != 0xE9) || result[i + 1] != marker) {
        ++i;
        continue;
      }

      var target = (uint)((result[i + 2] << 16) | (result[i + 3] << 8) | result[i + 4]);
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i + 1), unchecked(target - (uint)(i + 5)));
      i += 5;
    }

    return result;
  }

  /// <summary>
  /// Lays the decoded blocks back out at their original RVAs, producing the destination
  /// section as the stub leaves it. Gaps the packer never wrote stay zero.
  /// </summary>
  internal static byte[] AssembleImageRegion(Layout layout, List<Block> blocks) {
    var region = new byte[layout.DestinationSize];
    foreach (var block in blocks) {
      var offset = (int)(block.DestinationRva - layout.DestinationRva);
      var length = Math.Min(block.Data.Length, region.Length - offset);
      if (length > 0)
        block.Data.AsSpan(0, length).CopyTo(region.AsSpan(offset));
    }

    return region;
  }
}
