#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Dictionary.Aplib;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Real unpack handler for MEW (Northfox/HCC) — the "smallest PE packer", which
/// folds its whole first stage into the PE headers and marks its output section
/// <c>MEW</c>. Stage 1 is aPLib, stage 2 LZMA1; see <see cref="MewImage"/> for the
/// container layout.
/// </summary>
public sealed class MewExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
public override string Id => "mew";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public override string DisplayName => "MEW";

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public override ExecutableUnpackCapabilities Capabilities =>
    base.Capabilities | ExecutableUnpackCapabilities.CanDecompressPayload;

  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
protected override bool IsPackerSection(string name) =>
    name.StartsWith("MEW", StringComparison.OrdinalIgnoreCase) ||
    name.StartsWith(".MEW", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Gets the literal signature.
  /// </summary>
protected override ReadOnlySpan<byte> LiteralSignature => [];

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasMewSection = sections.Any(s => IsPackerSection(s.Name));
    return hasMewSection
      ? new(true, this.Id, 0.92, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "MEW section marker was not found.", true)]);
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    if (!MewImage.TryRead(packed.OriginalImage, options.MaximumDecompressedSize, out var layout) || layout is null)
      return this.UnpackLocatedOnly(packed, options);

    var payload = MewImage.Assemble(packed.OriginalImage, layout);
    if (payload.Length == 0)
      return this.UnpackLocatedOnly(packed, options);

    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
      new("decompressed_payload.bin", payload, layout.LzmaRecords > 0 ? "mew-aplib+lzma" : "mew-aplib"),
    };

    var caps = ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.CanDecompressPayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64)
      caps |= ExecutableUnpackCapabilities.SupportsX64;

    var level = ExecutableUnpackLevel.PayloadDecompressed;
    if (packed.ImageInfo is { Container: ExecutableContainerKind.Pe } info) {
      try {
        artifacts.Add(new("reconstructed/reconstructed.exe", PeRebuilder.RebuildSynthetic(info, payload), "stored"));
        level = ExecutableUnpackLevel.RebuiltExecutable;
        caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
      } catch {
        // A synthetic rebuild is a bonus; the decoded payload stands on its own.
      }
    }

    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        $"MEW payload decoded: {layout.AplibBlocks} aPLib block(s), {layout.LzmaRecords} LZMA record(s), original entry point RVA 0x{layout.EntryPointRva:X8}.",
        false),
      new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        "MEW discards the original PE headers and import directory; the decoded image is the mapped section content, not a byte-identical copy of the input file.",
        false),
    };

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  /// <summary>
  /// Fallback for images that carry the MEW section marker but no readable work
  /// list (a MEW build we do not model, or a tampered stub): carve the packed
  /// section so the payload is at least located.
  /// </summary>
  private UnpackResult UnpackLocatedOnly(PackedExecutable packed, UnpackOptions options) {
    var generic = base.Unpack(packed, options);
    if (generic.Level >= ExecutableUnpackLevel.PayloadLocated)
      return generic;

    var section = PackerScanner.GetPeSectionRanges(packed.OriginalImage)
      .Where(s => s.RawSize > 0 && s.RawOffset < packed.OriginalImage.Length)
      .OrderByDescending(s => s.RawSize)
      .FirstOrDefault();
    if (section.RawSize == 0)
      return generic;

    var artifacts = generic.Artifacts
      .Where(a => a.Name != "diagnostics.json")
      .ToList();
    var len = (int)Math.Min(section.RawSize, (uint)(packed.OriginalImage.Length - section.RawOffset));
    artifacts.Add(new("compressed_payload.bin", packed.OriginalImage.AsSpan((int)section.RawOffset, len).ToArray(), "mew-section"));

    var caps = ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64)
      caps |= ExecutableUnpackCapabilities.SupportsX64;

    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        "MEW packed section was located, but no readable MEW stage-1 work list was found.",
        true),
    };
    var result = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }
}

/// <summary>
/// Reader for the two-stage container MEW (Northfox/HCC, "MEW 11 SE") writes into
/// its packed PE. Layout below is derived from the stub the packer emits — MEW
/// hides its whole first stage inside the PE headers, so the loader code doubles
/// as the section table — and confirmed against the public PackingData corpus.
///
/// Stage 1 is a hand-written aPLib depacker (bit reader spliced into the first
/// section header's name field). Its work list lives at the start of the packed
/// section and the stub addresses it through the <c>mov esi, imm32</c> that opens
/// the stub, so the list is found by following that immediate rather than by
/// guessing an offset:
///
///   dword  address of the getbit thunk (stage-1 scratch, ignored here)
///   dword  original entry point, as a virtual address
///   repeat: dword destination virtual address (0 terminates)
///           bare aPLib stream, terminated by its own end-of-stream marker
///
/// Immediately after the terminator comes stage 2's work list: a pointer to the
/// probability-model scratch buffer followed by LZMA1 records
///
///   dword  uncompressed size (0 terminates)
///   dword  destination virtual address
///   dword  compressed size
///   byte   filler consumed by the stub before it seeds the range coder
///   ...    compressed size bytes of LZMA1, coded with lc=4, lp=0, pb=2
///
/// The properties are not stored anywhere: they are baked into the stage-2
/// decoder, which indexes literal probabilities by the top four bits of the
/// previous byte (lc=4), never by position (lp=0), and takes the position state
/// from the low two bits of the output position (pb=2).
///
/// Small inputs are packed without stage 2 at all; then the aPLib list alone
/// carries the image and the record table that follows it is not present.
/// </summary>
internal static class MewImage {
  /// <summary>
  /// Opening of the stage-1 stub: <c>mov ebx,esi / lodsd / lodsd / push eax /
  /// lodsd / xchg edi,eax / mov dl,0x80 / movsb / mov dh,0x80 / call [ebx]</c>.
  /// The <c>mov esi, imm32</c> that supplies the work-list address sits directly
  /// in front of it.
  /// </summary>
  private static ReadOnlySpan<byte> StubSignature =>
    [0x8B, 0xDE, 0xAD, 0xAD, 0x50, 0xAD, 0x97, 0xB2, 0x80, 0xA4, 0xB6, 0x80, 0xFF, 0x13];

  private const byte LoadEsiOpcode = 0xBE;

  /// <summary>LZMA1 properties byte for lc=4, lp=0, pb=2: <c>(pb * 5 + lp) * 9 + lc</c>.</summary>
  private const byte LzmaProperties = 94;

  private const int MaxBlocks = 256;

  internal readonly record struct MewPiece(uint Rva, byte[] Data, string Source);

  internal sealed record MewLayout(uint EntryPointRva, IReadOnlyList<MewPiece> Pieces, int AplibBlocks, int LzmaRecords);

  /// <summary>
  /// Walks both work lists and returns every decoded piece with the virtual
  /// address it is written to. Returns <see langword="false"/> when the image
  /// carries no readable MEW work list.
  /// </summary>
  public static bool TryRead(ReadOnlySpan<byte> image, long maximumDecompressedSize, out MewLayout? layout) {
    layout = null;
    if (!PeView.TryParse(image, out var pe))
      return false;

    var stub = image.IndexOf(StubSignature);
    if (stub < 5 || image[stub - 5] != LoadEsiOpcode)
      return false;

    var listVa = BinaryPrimitives.ReadUInt32LittleEndian(image[(stub - 4)..]);
    if (listVa < pe.ImageBase)
      return false;
    if (!pe.TryRvaToOffset((uint)(listVa - pe.ImageBase), out var cursor))
      return false;

    // Cap every single decode at the mapped image size: no MEW block can be
    // larger than the address space it is written into, and it keeps a corrupt
    // length field from turning into a gigabyte allocation.
    var cap = (int)Math.Min(maximumDecompressedSize <= 0 ? int.MaxValue : maximumDecompressedSize, Math.Max(pe.SizeOfImage, 0x10000u));

    if (cursor + 8 > image.Length)
      return false;
    cursor += 4; // getbit thunk address, only meaningful to the running stub
    var entryVa = BinaryPrimitives.ReadUInt32LittleEndian(image[cursor..]);
    cursor += 4;

    var pieces = new List<MewPiece>();
    var aplibBlocks = 0;
    while (cursor + 4 <= image.Length && aplibBlocks < MaxBlocks) {
      var destinationVa = BinaryPrimitives.ReadUInt32LittleEndian(image[cursor..]);
      cursor += 4;
      if (destinationVa == 0)
        break;
      if (destinationVa < pe.ImageBase)
        return false;

      byte[] block;
      int consumed;
      try {
        block = AplibBuildingBlock.DecompressRaw(image[cursor..], cap, out var endMarkerHit, out consumed);
        if (!endMarkerHit)
          return false;
      } catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) {
        return false;
      }

      cursor += consumed;
      pieces.Add(new((uint)(destinationVa - pe.ImageBase), block, "aplib"));
      ++aplibBlocks;
    }

    if (aplibBlocks == 0)
      return false;

    var lzmaRecords = ReadLzmaRecords(image, pe, cursor, cap, pieces);
    layout = new(entryVa >= pe.ImageBase ? (uint)(entryVa - pe.ImageBase) : 0, pieces, aplibBlocks, lzmaRecords);
    return true;
  }

  /// <summary>
  /// Reads the stage-2 record table that follows the aPLib work list. Inputs the
  /// packer decided were too small for stage 2 have no table here, so every field
  /// is range-checked and an implausible first record simply means "aPLib only".
  /// </summary>
  private static int ReadLzmaRecords(ReadOnlySpan<byte> image, in PeView pe, int cursor, int cap, List<MewPiece> pieces) {
    if (cursor + 4 > image.Length)
      return 0;
    cursor += 4; // probability-model scratch buffer address

    var records = 0;
    while (cursor + 12 <= image.Length && records < MaxBlocks) {
      var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(image[cursor..]);
      if (uncompressedSize == 0)
        break;
      var destinationVa = BinaryPrimitives.ReadUInt32LittleEndian(image[(cursor + 4)..]);
      var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(cursor + 8)..]);

      if (uncompressedSize > (uint)cap ||
          destinationVa < pe.ImageBase ||
          destinationVa - pe.ImageBase >= pe.SizeOfImage ||
          compressedSize == 0 ||
          (long)cursor + 13 + compressedSize > image.Length)
        break;

      byte[] decoded;
      try {
        decoded = DecodeLzma(image.Slice(cursor + 13, (int)compressedSize), uncompressedSize);
      } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IndexOutOfRangeException or ArgumentOutOfRangeException) {
        break;
      }
      if (decoded.Length != uncompressedSize)
        break;

      pieces.Add(new((uint)(destinationVa - pe.ImageBase), decoded, "lzma"));
      ++records;
      cursor += 13 + (int)compressedSize;
    }

    return records;
  }

  private static byte[] DecodeLzma(ReadOnlySpan<byte> compressed, uint uncompressedSize) {
    var properties = new byte[5];
    properties[0] = LzmaProperties;
    // The stub keeps the whole output addressable, so a window the size of the
    // output is exactly what the encoder could have referenced.
    BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(1), Math.Max(uncompressedSize, 1u << 16));
    using var input = new MemoryStream(compressed.ToArray(), false);
    return new LzmaDecoder(input, properties, uncompressedSize).Decode();
  }

  /// <summary>
  /// Flattens the decoded pieces into the section they are written to. MEW gives
  /// the unpacked image a section with no raw data at all — that is the one the
  /// original file's contents land in; pieces outside it are the loader's own
  /// scaffolding (import name list, stage-2 code) and are reported separately.
  /// </summary>
  public static byte[] Assemble(ReadOnlySpan<byte> image, MewLayout layout) {
    if (!PeView.TryParse(image, out var pe))
      return [];

    var target = pe.Sections
      .Where(s => s.RawSize == 0 && s.VirtualSize > 0)
      .OrderByDescending(s => s.VirtualSize)
      .FirstOrDefault();
    if (target.VirtualSize == 0) {
      var lowest = layout.Pieces.Min(p => p.Rva);
      var highest = layout.Pieces.Max(p => p.Rva + (uint)p.Data.Length);
      target = new(string.Empty, lowest, highest - lowest, 0, 0);
    }

    var buffer = new byte[target.VirtualSize];
    var wrote = false;
    foreach (var piece in layout.Pieces) {
      if (piece.Rva < target.VirtualAddress)
        continue;
      var offset = piece.Rva - target.VirtualAddress;
      if (offset + (uint)piece.Data.Length > target.VirtualSize)
        continue;
      piece.Data.CopyTo(buffer, (int)offset);
      wrote = true;
    }

    return wrote ? buffer : [];
  }
}

/// <summary>
/// Minimal PE header view. The packer handlers share
/// <see cref="PackerScanner"/> for section names and raw ranges, but MEW is
/// addressed entirely in virtual addresses, so this adds the RVA mapping the
/// shared scanner does not expose.
/// </summary>
internal readonly record struct PeSectionView(string Name, uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawOffset);

internal readonly record struct PeView(ulong ImageBase, uint SizeOfImage, uint EntryPointRva, IReadOnlyList<PeSectionView> Sections) {
  public static bool TryParse(ReadOnlySpan<byte> image, out PeView view) {
    view = default;
    if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
      return false;

    var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image[0x3C..]);
    if (peOffset < 0 || peOffset + 24 > image.Length)
      return false;
    if (BinaryPrimitives.ReadUInt32LittleEndian(image[peOffset..]) != 0x00004550)
      return false;

    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 6)..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 20)..]);
    var optional = peOffset + 24;
    if (optional + 68 > image.Length)
      return false;

    var magic = BinaryPrimitives.ReadUInt16LittleEndian(image[optional..]);
    var entry = BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 16)..]);
    ulong imageBase;
    uint sizeOfImage;
    if (magic == 0x20B) {
      if (optional + 80 > image.Length)
        return false;
      imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image[(optional + 24)..]);
      sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 56)..]);
    } else {
      imageBase = BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 28)..]);
      sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 56)..]);
    }

    var tableOffset = optional + optionalSize;
    if (sectionCount == 0 || tableOffset + sectionCount * 40 > image.Length)
      return false;

    var sections = new List<PeSectionView>(sectionCount);
    for (var i = 0; i < sectionCount; ++i) {
      var offset = tableOffset + i * 40;
      var nameSpan = image.Slice(offset, 8);
      var terminator = nameSpan.IndexOf((byte)0);
      var name = System.Text.Encoding.ASCII.GetString(terminator < 0 ? nameSpan : nameSpan[..terminator]);
      sections.Add(new(
        name,
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 12)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 8)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 16)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 20)..])));
    }

    view = new(imageBase, sizeOfImage, entry, sections);
    return true;
  }

  public bool TryRvaToOffset(uint rva, out int offset) {
    foreach (var section in this.Sections) {
      if (section.RawSize == 0)
        continue;
      if (rva < section.VirtualAddress || rva >= section.VirtualAddress + section.RawSize)
        continue;
      offset = (int)(section.RawOffset + (rva - section.VirtualAddress));
      return true;
    }

    offset = 0;
    return false;
  }
}
