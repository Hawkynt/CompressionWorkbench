#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Static unpacker for the Eronana Packer (github.com/Eronana/packer) — a
/// small educational Win32 PE packer whose section-compression codec is a
/// separate, fully-documented submodule (github.com/Eronana/compressor: a
/// hash-chain LZ77 matcher feeding a canonical Huffman coder).
/// </summary>
/// <remarks>
/// <para>
/// The packer strips the raw data of every non-skipped section (all sections
/// except those referenced by the resource/TLS data directories, or already
/// raw-data-less before packing), concatenates their original bytes, and
/// compresses the concatenation as one LZ77+Huffman stream. It appends a new
/// <c>".packer"</c> section holding, in order:
/// <c>[PEInfo][SectionInfo * NumberOfSections][compressed blob]…[IAT][shell
/// code][shell loader]</c>. Only the first three parts are needed to recover
/// the original image — the trailing shellcode is the runtime loader and is
/// not needed for a static decode.
/// </para>
/// <para>
/// <c>PEInfo</c> is 10 native <c>DWORD</c> fields (<c>ImageBase,
/// AddressOfEntryPoint, NumberOfSections, IIDVirtualAddress, NodeTotal,
/// UncompressSize, LoadLibraryA, GetProcAddress, VirtualAlloc,
/// VirtualFree</c>) followed by an always-empty trailing <c>data</c> struct
/// (the shipped <c>shell_data.h</c> template is fully commented out). An
/// empty C++ struct still occupies a minimum of one byte, and the compiler
/// pads the enclosing <c>PEInfo</c> up to its 4-byte alignment, so
/// <c>sizeof(PEInfo)</c> is 44 bytes, not 40 — confirmed by inspecting an
/// actual packed sample byte-for-byte (offsets 0..39 hold the ten documented
/// fields with sane values; offset 40..43 is the padded empty trailing
/// struct; the <c>SectionInfo</c> array only lines up correctly starting at
/// offset 44). Each <c>SectionInfo</c> is <c>{ DWORD VirtualAddress; DWORD
/// SizeOfRawData; }</c> (8 bytes), recording each stripped section's original
/// RVA and on-disk size in original section-table order.
/// </para>
/// <para>
/// The compressed blob is self-describing: <c>WORD tree_size; WORD
/// len_size_size; DWORD d_buf_size; DWORD l_buf_size;</c> followed by
/// <c>tree_size</c> canonical-Huffman symbol values, <c>len_size_size</c>
/// <c>{WORD len; WORD size;}</c> code-length groups (ascending by length),
/// then the MSB-first bit stream. Decoding replays the canonical Huffman
/// tree exactly as <c>compressor/uncompressor.cpp</c> documents it, producing
/// <c>d_buf_size</c> 16-bit tokens (values &lt; 256 are literal bytes; values
/// &gt;= 256 are LZ77 back-references, distance = value-256) plus
/// <c>l_buf_size</c> match-length bytes (actual length = byte + 3 =
/// <c>MIN_REPEAT_LENGTH</c>), which <c>unlz77</c> replays into a buffer of
/// <c>PEInfo.UncompressSize</c> bytes.
/// </para>
/// <para>
/// This decoder was validated byte-for-byte against a real sample built with
/// the actual Eronana packer (compiled from the published source): every
/// restored section matched the pre-pack original exactly.
/// </para>
/// <para>
/// The reconstructed image is a flattened, RVA-correct memory image (every
/// preserved section's original bytes, plus every stripped section's
/// decompressed bytes, placed at their true virtual addresses) wrapped in a
/// synthetic PE — the same convention the aPLib/NRV section handlers use.
/// <c>PEInfo.AddressOfEntryPoint</c> and <c>PEInfo.IIDVirtualAddress</c> (the
/// true original OEP and import-directory RVA) are reported in
/// <c>metadata.json</c> for the analyst, since they are not preserved by the
/// synthetic wrapper's own header.
/// </para>
/// </remarks>
public sealed class EronanaExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "eronanapacker";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Eronana Packer";

  private const string PackerSectionName = ".packer";
  private const int PeInfoSize = 44; // 10 DWORDs (40) + padded empty trailing struct (4)
  private const int MinRepeatLength = 3;

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanBuildMemoryImage |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Eronana: not a valid PE.", true)]);

    var ranges = PackerScanner.GetPeSectionRanges(image);
    var packerSection = ranges.FirstOrDefault(s => s.Name == PackerSectionName);
    if (packerSection.Name != PackerSectionName || packerSection.RawSize < PeInfoSize + 8)
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, $"Eronana: no \"{PackerSectionName}\" section (or too small to hold PEInfo).", true)]);

    var info = ExecutableContainerParsers.Pe.Parse(image);
    if (!TryReadPeInfo(image, (int)packerSection.RawOffset, out var peInfo))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Eronana: PEInfo failed basic bounds checks.", true)]);

    // Structural plausibility: ImageBase must match the PE's own image base,
    // NumberOfSections must be a sane subset of the real section count, and
    // UncompressSize must be positive and bounded by the sum of all section
    // virtual sizes — this rejects a coincidentally-named ".packer" section
    // that isn't really ours.
    var confidence = 0.5;
    if (peInfo.ImageBase == info.PreferredBaseAddress) confidence += 0.2;
    if (peInfo.NumberOfSections > 0 && peInfo.NumberOfSections < ranges.Count) confidence += 0.15;
    var totalVirtualSize = ranges.Aggregate(0UL, (acc, r) => acc + r.VirtualSize);
    if (peInfo.UncompressSize > 0 && (ulong)peInfo.UncompressSize <= totalVirtualSize) confidence += 0.15;

    return confidence >= 0.65
      ? new(true, this.Id, Math.Min(confidence, 1.0), [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Eronana: \".packer\" section present but PEInfo does not look structurally plausible.", true)]);
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
        ["packer"] = "Eronana Packer",
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    var diagnostics = new List<ExecutableDiagnostic>();
    var artifacts = new List<UnpackArtifact> { new("original_packed.bin", image, "stored") };

    var ranges = PackerScanner.GetPeSectionRanges(image);
    var packerSection = ranges.FirstOrDefault(s => s.Name == PackerSectionName);
    if (packerSection.Name != PackerSectionName) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "Eronana: \".packer\" section not found.", true));
      return Finish(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, artifacts, diagnostics, packed);
    }

    var packerRawOffset = (int)packerSection.RawOffset;
    if (!TryReadPeInfo(image, packerRawOffset, out var peInfo)) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "Eronana: PEInfo could not be read (section too small or malformed).", true));
      return Finish(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, artifacts, diagnostics, packed);
    }

    artifacts.Add(new("metadata.json", BuildMetadataJson(peInfo), "stored"));

    var sectionInfoOffset = packerRawOffset + PeInfoSize;
    var sectionInfoBytes = peInfo.NumberOfSections * 8;
    if (peInfo.NumberOfSections < 0 || peInfo.NumberOfSections > options.MaximumRegionCount ||
        sectionInfoOffset + sectionInfoBytes > image.Length) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "Eronana: SectionInfo table extends past EOF or exceeds configured limits.", true));
      return Finish(ExecutableUnpackLevel.PayloadLocated, ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.CanLocatePayload, artifacts, diagnostics, packed);
    }

    var sectionInfos = new (uint VirtualAddress, uint SizeOfRawData)[peInfo.NumberOfSections];
    for (var i = 0; i < peInfo.NumberOfSections; i++) {
      var off = sectionInfoOffset + i * 8;
      sectionInfos[i] = (
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off, 4)),
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 4, 4)));
    }

    var level = ExecutableUnpackLevel.PayloadLocated;
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.CanLocatePayload | ExecutableUnpackCapabilities.SupportsPe;

    if (peInfo.UncompressSize <= 0 || peInfo.UncompressSize > options.MaximumDecompressedSize) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "Eronana: PEInfo.UncompressSize is non-positive or exceeds the configured limit.", true));
      return Finish(level, caps, artifacts, diagnostics, packed);
    }

    var compressedStart = sectionInfoOffset + sectionInfoBytes;
    byte[] decompressed;
    try {
      decompressed = Decompress(image.AsSpan(compressedStart), peInfo.UncompressSize);
    } catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException) {
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed, $"Eronana: LZ77/Huffman decode failed: {ex.Message}", true));
      return Finish(level, caps, artifacts, diagnostics, packed);
    }

    artifacts.Add(new("decompressed_sections.bin", decompressed, "stored"));
    level = ExecutableUnpackLevel.PayloadDecompressed;
    caps |= ExecutableUnpackCapabilities.CanDecompressPayload;

    if (packed.ImageInfo is not { Container: ExecutableContainerKind.Pe } info) {
      diagnostics.Add(new(ExecutableDiagnosticCode.MemoryImageBuildFailed, "Eronana: PE container info unavailable for memory-image rebuild.", true));
      return Finish(level, caps, artifacts, diagnostics, packed);
    }

    // Splice each restored section's bytes back into its own region at the
    // recorded original virtual address; every other region (preserved
    // sections, the .packer section itself) is left as parsed.
    var pos = 0;
    var regions = info.Regions.ToList();
    foreach (var (va, size) in sectionInfos) {
      var idx = regions.FindIndex(r => r.VirtualAddress == va);
      if (idx < 0 || pos + size > decompressed.Length) {
        diagnostics.Add(new(ExecutableDiagnosticCode.MemoryImageBuildFailed, $"Eronana: no region matches SectionInfo RVA 0x{va:X}, or the decompressed data underruns.", true));
        continue;
      }
      var chunk = decompressed.AsSpan(pos, (int)size).ToArray();
      regions[idx] = regions[idx] with { FileBytes = chunk, MemoryBytes = chunk };
      pos += (int)size;
    }

    var patched = info with { Regions = regions };
    var (flatImage, _, buildDiagnostics) = ExecutableMemoryImageBuilder.Build(patched, options: options);
    diagnostics.AddRange(buildDiagnostics);
    if (flatImage == null)
      return Finish(level, caps, artifacts, diagnostics, packed);

    artifacts.Add(new("memory_image.bin", flatImage, "stored"));
    level = ExecutableUnpackLevel.RuntimeMemoryImage;
    caps |= ExecutableUnpackCapabilities.CanBuildMemoryImage;

    try {
      var rebuilt = PeRebuilder.RebuildSynthetic(patched, flatImage);
      artifacts.Add(new("reconstructed/reconstructed.exe", rebuilt, "stored"));
      level = ExecutableUnpackLevel.RebuiltExecutable;
      caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
      diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        $"Eronana: rebuilt PE wraps the recovered RVA-mapped memory image, not a byte-identical re-serialization of the pre-pack file; the true original entry point (0x{peInfo.AddressOfEntryPoint:X}) and import-directory RVA (0x{peInfo.IIDVirtualAddress:X}) are reported in metadata.json for the analyst."));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) {
      diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed, $"Eronana: PE reconstruction failed: {ex.Message}", options.StrictRebuild));
    }

    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;
    return Finish(level, caps, artifacts, diagnostics, packed);
  }

  private static UnpackResult Finish(ExecutableUnpackLevel level, ExecutableUnpackCapabilities caps, List<UnpackArtifact> artifacts, List<ExecutableDiagnostic> diagnostics, PackedExecutable packed) {
    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build("eronanapacker", packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private readonly record struct PeInfo(
    uint ImageBase, uint AddressOfEntryPoint, int NumberOfSections,
    uint IIDVirtualAddress, uint NodeTotal, int UncompressSize);

  private static bool TryReadPeInfo(ReadOnlySpan<byte> image, int offset, out PeInfo peInfo) {
    peInfo = default;
    if (offset < 0 || offset + PeInfoSize > image.Length) return false;
    var imageBase = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset, 4));
    var aep = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset + 4, 4));
    var numSections = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(offset + 8, 4));
    var iidVa = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset + 12, 4));
    var nodeTotal = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset + 16, 4));
    var uncompressSize = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(offset + 20, 4));
    peInfo = new(imageBase, aep, numSections, iidVa, nodeTotal, uncompressSize);
    return true;
  }

  /// <summary>
  /// Decodes an Eronana <c>compressor</c>-format stream (canonical Huffman
  /// over a stream of LZ77 literal/back-reference tokens), matching
  /// <c>uncompressor.cpp</c>'s <c>uncompress()</c>/<c>unlz77()</c> exactly.
  /// </summary>
  private static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressSize) {
    var pos = 0;
    var treeSize = BinaryPrimitives.ReadUInt16LittleEndian(src[pos..]); pos += 2;
    var lenSizeCount = BinaryPrimitives.ReadUInt16LittleEndian(src[pos..]); pos += 2;
    var dBufSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(src[pos..])); pos += 4;
    var lBufSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(src[pos..])); pos += 4;

    var tree = new int[treeSize];
    for (var i = 0; i < treeSize; i++) {
      tree[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[pos..]);
      pos += 2;
    }

    var lenSize = new (int Len, int Size)[lenSizeCount];
    for (var i = 0; i < lenSizeCount; i++) {
      var len = BinaryPrimitives.ReadUInt16LittleEndian(src[pos..]);
      var size = BinaryPrimitives.ReadUInt16LittleEndian(src[(pos + 2)..]);
      lenSize[i] = (len, size);
      pos += 4;
    }

    var bitStream = src[pos..].ToArray();

    var root = new HuffNode();
    var code = 0;
    var lastLen = 0;
    var a = 0;
    foreach (var (len, size) in lenSize) {
      code <<= len - lastLen;
      lastLen = len;
      for (var s = 0; s < size; s++)
        Build(root, tree[a++], len, code++);
    }

    var byteCount = 0;
    int DecodeOne() {
      var node = root;
      while (node.Value < 0) {
        var b = bitStream[byteCount >> 3];
        var bit = (b >> (7 - (byteCount & 7))) & 1;
        node = bit == 0 ? node.Child0! : node.Child1!;
        byteCount++;
      }
      return node.Value;
    }

    var dBuf = new int[dBufSize];
    for (var i = 0; i < dBufSize; i++) dBuf[i] = DecodeOne();
    var lBuf = new byte[lBufSize];
    for (var i = 0; i < lBufSize; i++) lBuf[i] = (byte)DecodeOne();

    var dest = new byte[uncompressSize];
    var x = 0;
    var next = 0;
    for (var i = 0; i < dBufSize; i++) {
      var dis = dBuf[i] - 256;
      if (dis < 0)
        dest[next++] = (byte)dBuf[i];
      else {
        var srcPos = next - dis;
        var length = lBuf[x++] + MinRepeatLength;
        for (var j = 0; j < length; j++)
          dest[next++] = dest[srcPos + j];
      }
    }
    return dest;
  }

  private sealed class HuffNode {
    public int Value = -1;
    public HuffNode? Child0;
    public HuffNode? Child1;
  }

  private static void Build(HuffNode root, int value, int len, int code) {
    var node = root;
    for (var i = len - 1; i >= 0; i--) {
      var bit = (code >> i) & 1;
      node = bit == 0 ? node.Child0 ??= new HuffNode() : node.Child1 ??= new HuffNode();
    }
    node.Value = value;
  }

  private static byte[] BuildMetadataJson(PeInfo peInfo) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"eronanapacker\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageBase\": \"0x{peInfo.ImageBase:X}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"originalEntryPointRva\": \"0x{peInfo.AddressOfEntryPoint:X}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"originalImportDirectoryRva\": \"0x{peInfo.IIDVirtualAddress:X}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"numberOfPackedSections\": {peInfo.NumberOfSections},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"uncompressSize\": {peInfo.UncompressSize}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
