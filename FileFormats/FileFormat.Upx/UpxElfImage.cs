#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Dictionary.Nrv2b;
using Compression.Core.Dictionary.Nrv2d;
using Compression.Core.Dictionary.Nrv2e;

namespace FileFormat.Upx;

/// <summary>
/// Parser and rebuilder for the ELF flavour of the UPX container.
/// </summary>
/// <remarks>
/// <para>
/// The ELF container is arranged quite differently from the PE one. A PE image
/// carries a single compressed payload followed by one trailing
/// <c>PackHeader</c>; an ELF image instead carries <em>two chains</em> of
/// per-block headers, and the original file is reassembled by interleaving
/// them. The layout, as observed across the ELF sample corpus, is:
/// </para>
/// <code>
///   [ELF header + program headers of the stub]
///   [l_info ]  u32 checksum, "UPX!", u16 loader_size, u8 version, u8 format
///   [p_info ]  u32 program_id, u32 original_file_size, u32 block_size
///   [main chain: 1 + N blocks, each b_info + data]
///        block 0      = the original ELF header + program headers
///        block 1      = the remainder of the original PT_LOAD[0] after them
///        block k >= 2 = the file contents of the original PT_LOAD[k-1]
///   [stub / loader code]
///   [hole chain, 4-byte aligned after the packed PT_LOAD[0] file size]
///        one block per non-empty gap between consecutive original PT_LOADs,
///        then one for whatever trails the last PT_LOAD (section headers, …)
///   [trailing 32-byte PackHeader]
/// </code>
/// <para>
/// A <c>b_info</c> is <c>u32 uncompressed_size, u32 compressed_size, u8 method,
/// u8 filter_id, u8 filter_cto, u8 unused</c>. A block whose compressed size is
/// not smaller than its uncompressed size is stored verbatim.
/// </para>
/// <para>
/// Neither chain is self-terminating in a way that can be relied upon: some
/// images end the main chain with an all-zero <c>b_info</c> and some simply run
/// straight into the stub. The block count is therefore taken from the original
/// program-header table, which is what block 0 decompresses to — the container
/// describes its own shape once that first block is unpacked.
/// </para>
/// </remarks>
public static class UpxElfImage {

  /// <summary>One compressed (or stored) block plus the b_info that describes it.</summary>
  public sealed record Block(
    int HeaderOffset,
    int DataOffset,
    uint UncompressedSize,
    uint CompressedSize,
    byte Method,
    byte FilterId,
    byte FilterCto,
    bool Stored);

  /// <summary>A PT_LOAD of the original (unpacked) image, in file-offset order.</summary>
  public sealed record LoadSegment(long FileOffset, long FileSize);

  public sealed record Image(
    byte Version,
    byte Format,
    ushort LoaderSize,
    uint OriginalFileSize,
    uint BlockSize,
    IReadOnlyList<Block> Blocks,
    IReadOnlyList<Block> HoleBlocks,
    IReadOnlyList<LoadSegment> OriginalLoads,
    IReadOnlyList<byte[]> BlockData,
    IReadOnlyList<byte[]> HoleData,
    byte[] Payload,
    byte[]? Original,
    IReadOnlyList<string> Notes);

  private const int Elf64HeaderSize = 64;
  private const int Elf32HeaderSize = 52;
  private const uint PtLoad = 1;

  /// <summary>
  /// Parses the UPX ELF container and decompresses every block. Returns
  /// <see langword="null"/> when the image is not a UPX ELF container we can
  /// follow; <paramref name="error"/> then carries the reason.
  /// </summary>
  public static Image? TryRead(ReadOnlySpan<byte> image, long maximumDecompressedSize, out string? error) {
    error = null;
    if (image.Length < Elf32HeaderSize || image[0] != 0x7F || image[1] != 'E' || image[2] != 'L' || image[3] != 'F') {
      error = "Not an ELF image.";
      return null;
    }

    var is64 = image[4] == 2;
    if (image[4] is not (1 or 2)) { error = "Unknown ELF class."; return null; }
    if (image[5] != 1) { error = "Only little-endian ELF images are supported."; return null; }
    if (is64 && image.Length < Elf64HeaderSize) { error = "Truncated ELF header."; return null; }

    if (!TryReadProgramHeaderTable(image, is64, out var phoff, out var phentsize, out var phnum, out error))
      return null;

    // l_info sits immediately behind the stub's own program-header table.
    var lInfo = phoff + (long)phentsize * phnum;
    if (lInfo < 0 || lInfo + 12 > image.Length) { error = "UPX ELF l_info is outside the image."; return null; }
    var l = (int)lInfo;
    if (image[l + 4] != 'U' || image[l + 5] != 'P' || image[l + 6] != 'X' || image[l + 7] != '!') {
      error = "No UPX ELF l_info block behind the program-header table.";
      return null;
    }

    var loaderSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(l + 8)..]);
    var version = image[l + 10];
    var format = image[l + 11];

    var p = l + 12;
    if (p + 12 > image.Length) { error = "UPX ELF p_info is outside the image."; return null; }
    var originalFileSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(p + 4)..]);
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(p + 8)..]);
    if (originalFileSize == 0) { error = "UPX ELF p_info reports a zero original file size."; return null; }
    if (originalFileSize > maximumDecompressedSize || originalFileSize > int.MaxValue) {
      error = "UPX ELF original size exceeds the configured executable unpacking limit.";
      return null;
    }

    var notes = new List<string>();
    var cursor = p + 12;

    // Block 0 is the original ELF header plus its program headers; everything
    // else about the layout is derived from what it decodes to.
    if (!TryReadBlock(image, ref cursor, out var headerBlock, out error)) return null;
    if (!TryDecodeBlock(image, headerBlock, originalFileSize, out var headerBytes, out error)) return null;

    if (!TryReadProgramHeaderTable(headerBytes, headerBytes[4] == 2, out var ophoff, out var ophentsize, out var ophnum, out error))
      return null;
    var originalHeaderSize = (headerBytes[4] == 2 ? Elf64HeaderSize : Elf32HeaderSize) + (long)ophentsize * ophnum;
    if (originalHeaderSize != headerBytes.Length) {
      error = $"UPX ELF header block is {headerBytes.Length} bytes but its own program-header table describes {originalHeaderSize}.";
      return null;
    }

    var loads = ReadLoadSegments(headerBytes, headerBytes[4] == 2, ophoff, ophentsize, ophnum);
    if (loads.Count == 0) { error = "The original ELF image has no loadable segment."; return null; }
    if (loads[0].FileOffset != 0) {
      error = $"The original ELF image starts its first PT_LOAD at 0x{loads[0].FileOffset:X} rather than 0; this layout is not handled.";
      return null;
    }
    if (loads[0].FileSize < headerBytes.Length) {
      error = "The original ELF image has a first PT_LOAD smaller than its own header.";
      return null;
    }
    // The segment table comes out of a compressed block, so it is as
    // attacker-controlled as the rest. Every segment has to lie inside the file
    // it claims to belong to before its size is used to size a buffer.
    foreach (var load in loads)
      if (load.FileOffset < 0 || load.FileSize < 0 || load.FileOffset + load.FileSize > originalFileSize) {
        error = $"The original ELF image has a PT_LOAD at 0x{load.FileOffset:X} of 0x{load.FileSize:X} bytes that runs past its own {originalFileSize}-byte extent.";
        return null;
      }

    // Main chain: the header block, the tail of PT_LOAD[0], then one block per
    // remaining PT_LOAD.
    var blocks = new List<Block> { headerBlock };
    var blockData = new List<byte[]> { headerBytes };
    var expected = new List<long> { loads[0].FileSize - headerBytes.Length };
    for (var i = 1; i < loads.Count; i++) expected.Add(loads[i].FileSize);

    foreach (var want in expected) {
      // A segment that is exactly the header carries no b_info of its own; keep
      // the descriptor lists in step so callers can pair them up by index.
      if (want == 0) {
        blocks.Add(new Block(0, 0, 0, 0, 0, 0, 0, Stored: true));
        blockData.Add([]);
        continue;
      }
      if (!TryReadBlock(image, ref cursor, out var block, out error)) return null;
      if (block.UncompressedSize != want) {
        error = $"UPX ELF block at 0x{block.HeaderOffset:X} reports {block.UncompressedSize} bytes where the program-header table requires {want}.";
        return null;
      }
      if (!TryDecodeBlock(image, block, originalFileSize, out var data, out error)) return null;
      blocks.Add(block);
      blockData.Add(data);
    }

    // Hole chain: the bytes of the original that no PT_LOAD covers. It starts
    // 4-byte aligned behind the packed image's own first PT_LOAD.
    var holeSizes = new List<long>();
    for (var i = 0; i + 1 < loads.Count; i++)
      holeSizes.Add(loads[i + 1].FileOffset - (loads[i].FileOffset + loads[i].FileSize));
    holeSizes.Add(originalFileSize - (loads[^1].FileOffset + loads[^1].FileSize));

    var holeBlocks = new List<Block>();
    var holeData = new List<byte[]>();
    var holesComplete = true;
    if (!TryReadPackedLoadFileSize(image, is64, phoff, phentsize, phnum, out var packedLoadSize)) {
      notes.Add("The packed image has no PT_LOAD to anchor the hole chain; only the loadable segments were recovered.");
      holesComplete = false;
    } else {
      var holeCursor = (int)((packedLoadSize + 3) & ~3L);
      foreach (var want in holeSizes) {
        if (want < 0) { holesComplete = false; notes.Add("The original program-header table describes overlapping PT_LOADs."); break; }
        if (want == 0) { holeData.Add([]); continue; }
        if (!TryReadBlock(image, ref holeCursor, out var block, out var holeError) || block.UncompressedSize != want) {
          holesComplete = false;
          notes.Add(holeError ?? $"No hole block describing {want} bytes was found where the container places it.");
          break;
        }
        if (!TryDecodeBlock(image, block, originalFileSize, out var data, out holeError)) {
          holesComplete = false;
          notes.Add(holeError!);
          break;
        }
        holeBlocks.Add(block);
        holeData.Add(data);
      }
    }

    var payload = Concat(blockData);
    byte[]? original = null;
    if (holesComplete && holeData.Count == holeSizes.Count)
      original = Reassemble(originalFileSize, loads, blockData, holeData, notes);
    else
      notes.Add("The blocks that no PT_LOAD covers could not be recovered, so the original file was not rebuilt.");

    return new Image(
      Version: version,
      Format: format,
      LoaderSize: loaderSize,
      OriginalFileSize: originalFileSize,
      BlockSize: blockSize,
      Blocks: blocks,
      HoleBlocks: holeBlocks,
      OriginalLoads: loads,
      BlockData: blockData,
      HoleData: holeData,
      Payload: payload,
      Original: original,
      Notes: notes);
  }

  /// <summary>
  /// Lays the recovered blocks back out at the file offsets the original
  /// program-header table gives them, filling the gaps from the hole chain.
  /// </summary>
  private static byte[]? Reassemble(
      uint originalFileSize,
      IReadOnlyList<LoadSegment> loads,
      IReadOnlyList<byte[]> blockData,
      IReadOnlyList<byte[]> holeData,
      List<string> notes) {
    var original = new byte[originalFileSize];

    // Block 0 is the header, block 1 the rest of PT_LOAD[0], and from there one
    // block per PT_LOAD.
    if (!TryCopy(original, 0, blockData[0], notes)) return null;
    if (!TryCopy(original, blockData[0].Length, blockData[1], notes)) return null;
    for (var i = 1; i < loads.Count; i++)
      if (!TryCopy(original, loads[i].FileOffset, blockData[i + 1], notes)) return null;

    for (var i = 0; i < holeData.Count; i++) {
      var at = loads[i].FileOffset + loads[i].FileSize;
      if (!TryCopy(original, at, holeData[i], notes)) return null;
    }

    return original;
  }

  private static bool TryCopy(byte[] destination, long offset, byte[] source, List<string> notes) {
    if (offset < 0 || offset + source.Length > destination.Length) {
      notes.Add($"A recovered block of {source.Length} bytes does not fit the original image at 0x{offset:X}.");
      return false;
    }
    source.CopyTo(destination, (int)offset);
    return true;
  }

  private static byte[] Concat(IReadOnlyList<byte[]> parts) {
    var total = 0;
    foreach (var p in parts) total += p.Length;
    var result = new byte[total];
    var at = 0;
    foreach (var p in parts) { p.CopyTo(result, at); at += p.Length; }
    return result;
  }

  private static bool TryReadProgramHeaderTable(
      ReadOnlySpan<byte> image, bool is64, out long phoff, out int phentsize, out int phnum, out string? error) {
    phoff = 0; phentsize = 0; phnum = 0; error = null;
    var headerSize = is64 ? Elf64HeaderSize : Elf32HeaderSize;
    if (image.Length < headerSize) { error = "Truncated ELF header."; return false; }

    phoff = is64
      ? (long)BinaryPrimitives.ReadUInt64LittleEndian(image[0x20..])
      : BinaryPrimitives.ReadUInt32LittleEndian(image[0x1C..]);
    phentsize = BinaryPrimitives.ReadUInt16LittleEndian(image[(is64 ? 0x36 : 0x2A)..]);
    phnum = BinaryPrimitives.ReadUInt16LittleEndian(image[(is64 ? 0x38 : 0x2C)..]);

    var minimum = is64 ? 56 : 32;
    if (phentsize < minimum || phnum == 0 || phoff < headerSize || phoff + (long)phentsize * phnum > image.Length) {
      error = "The ELF program-header table is missing or outside the image.";
      return false;
    }
    return true;
  }

  private static List<LoadSegment> ReadLoadSegments(
      ReadOnlySpan<byte> image, bool is64, long phoff, int phentsize, int phnum) {
    var loads = new List<LoadSegment>();
    for (var i = 0; i < phnum; i++) {
      var o = (int)(phoff + (long)phentsize * i);
      if (BinaryPrimitives.ReadUInt32LittleEndian(image[o..]) != PtLoad) continue;
      var offset = is64
        ? (long)BinaryPrimitives.ReadUInt64LittleEndian(image[(o + 8)..])
        : BinaryPrimitives.ReadUInt32LittleEndian(image[(o + 4)..]);
      var fileSize = is64
        ? (long)BinaryPrimitives.ReadUInt64LittleEndian(image[(o + 32)..])
        : BinaryPrimitives.ReadUInt32LittleEndian(image[(o + 16)..]);
      // A PT_LOAD with no file content is pure .bss and contributes no block.
      if (fileSize > 0) loads.Add(new(offset, fileSize));
    }
    loads.Sort((a, b) => a.FileOffset.CompareTo(b.FileOffset));
    return loads;
  }

  private static bool TryReadPackedLoadFileSize(
      ReadOnlySpan<byte> image, bool is64, long phoff, int phentsize, int phnum, out long fileSize) {
    fileSize = 0;
    for (var i = 0; i < phnum; i++) {
      var o = (int)(phoff + (long)phentsize * i);
      if (BinaryPrimitives.ReadUInt32LittleEndian(image[o..]) != PtLoad) continue;
      fileSize = is64
        ? (long)BinaryPrimitives.ReadUInt64LittleEndian(image[(o + 32)..])
        : BinaryPrimitives.ReadUInt32LittleEndian(image[(o + 16)..]);
      return fileSize > 0 && fileSize <= image.Length;
    }
    return false;
  }

  private static bool TryReadBlock(ReadOnlySpan<byte> image, ref int cursor, out Block block, out string? error) {
    block = null!;
    error = null;
    if (cursor < 0 || cursor + 12 > image.Length) { error = "A UPX ELF b_info runs past the end of the image."; return false; }

    var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(image[cursor..]);
    var compressed = BinaryPrimitives.ReadUInt32LittleEndian(image[(cursor + 4)..]);
    var method = image[cursor + 8];
    var filterId = image[cursor + 9];
    var filterCto = image[cursor + 10];

    if (uncompressed == 0) { error = $"The UPX ELF block chain ends at 0x{cursor:X} before every segment was described."; return false; }

    // A block that did not get smaller is kept verbatim.
    var stored = compressed >= uncompressed || compressed == 0;
    var dataLength = stored ? uncompressed : compressed;
    var dataOffset = cursor + 12;
    if (dataOffset + (long)dataLength > image.Length) {
      error = $"The UPX ELF block at 0x{cursor:X} claims {dataLength} bytes of data past the end of the image.";
      return false;
    }

    block = new Block(cursor, dataOffset, uncompressed, compressed, method, filterId, filterCto, stored);
    cursor = dataOffset + (int)dataLength;
    return true;
  }

  private static bool TryDecodeBlock(
      ReadOnlySpan<byte> image, Block block, long maximumBlockSize, out byte[] data, out string? error) {
    data = [];
    error = null;

    // Every block is a slice of the original file, so none of them can be
    // larger than it. The b_info is attacker-controlled and this value sizes an
    // allocation, so it has to be bounded before it is believed.
    if (block.UncompressedSize > maximumBlockSize) {
      error = $"The UPX ELF block at 0x{block.HeaderOffset:X} claims {block.UncompressedSize} bytes, more than the {maximumBlockSize}-byte image it is part of.";
      return false;
    }

    var size = (int)block.UncompressedSize;

    if (block.Stored)
      data = image.Slice(block.DataOffset, size).ToArray();
    else {
      var compressed = image.Slice(block.DataOffset, checked((int)block.CompressedSize)).ToArray();
      try {
        data = block.Method switch {
          2 => Nrv2bBuildingBlock.DecompressRaw(compressed, size),
          4 => Nrv2bBuildingBlock.DecompressRawLe16(compressed, size),
          6 => Nrv2bBuildingBlock.DecompressRawByte(compressed, size),
          3 => Nrv2dBuildingBlock.DecompressRaw(compressed, size),
          5 => Nrv2dBuildingBlock.DecompressRawLe16(compressed, size),
          7 => Nrv2dBuildingBlock.DecompressRawByte(compressed, size),
          8 => Nrv2eBuildingBlock.DecompressRaw(compressed, size),
          9 => Nrv2eBuildingBlock.DecompressRawLe16(compressed, size),
          10 => Nrv2eBuildingBlock.DecompressRawByte(compressed, size),
          _ => throw new NotSupportedException($"UPX compression method {block.Method} ({UpxReader.MethodName(block.Method)}) is not supported for ELF blocks."),
        };
      } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException
          or ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException) {
        error = $"UPX ELF block at 0x{block.HeaderOffset:X} ({UpxReader.MethodName(block.Method)}) failed to decompress: {ex.Message}";
        return false;
      }
    }

    if (block.FilterId != 0 && !UpxFilters.TryReverse(data, block.FilterId, block.FilterCto, out var filterError)) {
      error = filterError;
      return false;
    }

    return true;
  }
}
