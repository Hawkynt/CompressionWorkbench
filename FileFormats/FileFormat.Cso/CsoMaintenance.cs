#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace FileFormat.Cso;

/// <summary>
/// Canonical block/index repacker for CSO/ZSO images.
/// </summary>
/// <remarks>
/// <para>CSO v0/v1 blocks can be decoded with raw DEFLATE and are recompressed
/// one block at a time. This closes orphaned byte ranges left behind by the
/// random-access editor without materialising the whole ISO.</para>
/// <para>ZSO and CSO v2 use compression semantics the creator does not reproduce.
/// Their indexed block slots are therefore copied verbatim while offsets are
/// canonicalised. This still removes bytes outside the indexed data segment and
/// preserves every block exactly.</para>
/// </remarks>
internal static class CsoMaintenance {
  private const uint Flag = 0x8000_0000u;
  private const uint OffsetMask = 0x7FFF_FFFFu;
  private const int HeaderSize = 24;

  private sealed record Layout(
    byte[] Header,
    string Magic,
    ulong UncompressedSize,
    int BlockSize,
    byte Version,
    byte Align,
    int BlockCount,
    uint[] Index,
    long FileLength) {
    public bool IsClassicCso => Magic == "CISO" && Version <= 1;
  }

  public static void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException("CSO/ZSO maintenance supports only ConsolidateAtStart.");
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("CSO/ZSO defragmentation requires a readable, writable, seekable stream.", nameof(archive));

    options.CancellationToken.ThrowIfCancellationRequested();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, null, "Scanning CSO/ZSO block index"));

    using var staged = CreateScratchStream();
    Repack(archive, staged, options.CancellationToken, options.OnProgress);

    staged.Position = 0;
    _ = new CsoFormatDescriptor().List(staged, null);
    options.CancellationToken.ThrowIfCancellationRequested();

    archive.Position = 0;
    archive.SetLength(0);
    staged.Position = 0;
    staged.CopyTo(archive);
    archive.Flush();
    archive.Position = 0;

    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, null, "CSO/ZSO block index compacted"));
  }

  public static void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("CSO/ZSO shrink requires a readable, seekable input.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("CSO/ZSO shrink requires a writable, seekable output.", nameof(output));

    var originalLength = input.Length;
    using var staged = CreateScratchStream();
    Repack(input, staged, CancellationToken.None, null);
    staged.Position = 0;
    _ = new CsoFormatDescriptor().List(staged, null);

    if (ReferenceEquals(input, output)) {
      if (staged.Length >= originalLength) {
        input.Position = 0;
        return;
      }
      output.Position = 0;
      output.SetLength(0);
      staged.Position = 0;
      staged.CopyTo(output);
      output.Position = 0;
      return;
    }

    output.Position = 0;
    output.SetLength(0);
    if (staged.Length < originalLength) {
      staged.Position = 0;
      staged.CopyTo(output);
    } else {
      input.Position = 0;
      input.CopyTo(output);
    }
    output.Position = 0;
  }

  private static void Repack(
      Stream input,
      Stream output,
      CancellationToken cancellationToken,
      Action<DefragProgressEvent>? onProgress) {
    var layout = ReadLayout(input);
    var alignment = Alignment(layout.Align);
    var indexBytes = checked((layout.BlockCount + 1) * 4L);
    var minimumDataOffset = checked(HeaderSize + indexBytes);

    output.Position = 0;
    output.SetLength(0);
    output.Write(layout.Header);
    WriteZeros(output, indexBytes);
    PadToAlignment(output, alignment);

    var rewrittenIndex = new uint[layout.BlockCount + 1];
    var totalBlocks = Math.Max(1, layout.BlockCount);

    for (var blockIndex = 0; blockIndex < layout.BlockCount; ++blockIndex) {
      cancellationToken.ThrowIfCancellationRequested();
      var (sourceOffset, sourceEnd, sourceFlag) = BlockSpan(layout, blockIndex);
      if (sourceOffset < minimumDataOffset)
        throw new InvalidDataException($"CSO/ZSO block {blockIndex} overlaps the header/index area.");

      PadToAlignment(output, alignment);
      var targetOffset = output.Position;
      rewrittenIndex[blockIndex] = EncodeIndex(targetOffset, layout.Align, sourceFlag);

      if (layout.IsClassicCso) {
        var logicalLength = checked((int)Math.Min(
          (ulong)layout.BlockSize,
          layout.UncompressedSize - Math.Min(layout.UncompressedSize, (ulong)blockIndex * (ulong)layout.BlockSize)));
        var slab = DecodeClassicBlock(input, sourceOffset, sourceEnd - sourceOffset,
          sourceFlag, layout.BlockSize, logicalLength);
        var compressed = CsoWriter.Deflate(slab);
        if (compressed.Length < layout.BlockSize) {
          rewrittenIndex[blockIndex] = EncodeIndex(targetOffset, layout.Align, false);
          output.Write(compressed);
        } else {
          rewrittenIndex[blockIndex] = EncodeIndex(targetOffset, layout.Align, true);
          output.Write(slab);
        }
      } else {
        CopyRange(input, output, sourceOffset, sourceEnd - sourceOffset);
      }

      PadToAlignment(output, alignment);
      onProgress?.Invoke(new DefragProgressEvent(
        "writing", (blockIndex + 1d) / totalBlocks,
        sourceEnd, output.Position, Math.Max(layout.FileLength, output.Length), null,
        $"Repacked block {blockIndex + 1:N0}/{layout.BlockCount:N0}"));
    }

    rewrittenIndex[layout.BlockCount] = EncodeIndex(output.Position, layout.Align, false);
    var finalLength = output.Position;

    output.Position = HeaderSize;
    Span<byte> word = stackalloc byte[4];
    foreach (var entry in rewrittenIndex) {
      BinaryPrimitives.WriteUInt32LittleEndian(word, entry);
      output.Write(word);
    }
    output.SetLength(finalLength);
    output.Position = 0;
  }

  private static Layout ReadLayout(Stream input) {
    input.Position = 0;
    var header = new byte[HeaderSize];
    input.ReadExactly(header);
    var magic = System.Text.Encoding.ASCII.GetString(header, 0, 4);
    if (magic is not ("CISO" or "ZISO"))
      throw new InvalidDataException("Not a CSO/ZSO image.");

    var uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8, 8));
    var blockSizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));
    if (blockSizeRaw is 0 or > int.MaxValue)
      throw new InvalidDataException($"Invalid CSO/ZSO block size {blockSizeRaw}.");
    var blockSize = (int)blockSizeRaw;
    var version = header[20];
    var align = header[21];
    _ = Alignment(align); // validate before shifting index values below

    var blockCount64 = (uncompressedSize + blockSizeRaw - 1) / blockSizeRaw;
    if (blockCount64 > 8_000_000)
      throw new InvalidDataException($"CSO/ZSO block count implausible: {blockCount64}.");
    var blockCount = checked((int)blockCount64);
    var index = new uint[blockCount + 1];
    Span<byte> word = stackalloc byte[4];
    input.Position = HeaderSize;
    for (var i = 0; i < index.Length; ++i) {
      input.ReadExactly(word);
      index[i] = BinaryPrimitives.ReadUInt32LittleEndian(word);
    }

    return new Layout(header, magic, uncompressedSize, blockSize, version, align,
      blockCount, index, input.Length);
  }

  private static (long Start, long End, bool Flag) BlockSpan(Layout layout, int index) {
    var raw = layout.Index[index];
    var next = layout.Index[index + 1];
    var start = checked((long)(raw & OffsetMask) << layout.Align);
    var end = checked((long)(next & OffsetMask) << layout.Align);
    if (end < start)
      throw new InvalidDataException($"CSO/ZSO block {index} has a decreasing index entry.");
    if (start < 0 || end > layout.FileLength)
      throw new InvalidDataException($"CSO/ZSO block {index} lies outside the file.");
    return (start, end, (raw & Flag) != 0);
  }

  private static byte[] DecodeClassicBlock(
      Stream input,
      long offset,
      long storedLength,
      bool uncompressed,
      int blockSize,
      int logicalLength) {
    if (storedLength < 0)
      throw new InvalidDataException("CSO block has a negative stored length.");

    var slab = new byte[blockSize];
    input.Position = offset;
    if (uncompressed) {
      var required = Math.Min(blockSize, logicalLength);
      var available = Math.Min(storedLength, blockSize);
      if (available < required)
        throw new InvalidDataException("Uncompressed CSO block is shorter than its logical payload.");
      ReadExactly(input, slab.AsSpan(0, checked((int)available)));
      return slab;
    }

    using var bounded = new BoundedEntryStream(input, storedLength, leaveOpen: true);
    using var inflater = new DeflateStream(bounded, CompressionMode.Decompress, leaveOpen: true);
    var written = 0;
    while (written < slab.Length) {
      var read = inflater.Read(slab.AsSpan(written));
      if (read <= 0) break;
      written += read;
    }
    if (written < logicalLength)
      throw new InvalidDataException(
        $"Compressed CSO block decoded to {written} bytes, fewer than the {logicalLength} logical bytes required.");
    return slab;
  }

  private static uint EncodeIndex(long physicalOffset, byte align, bool flag) {
    var alignment = Alignment(align);
    if ((physicalOffset & (alignment - 1)) != 0)
      throw new InvalidOperationException("CSO/ZSO output offset is not representable at the declared index shift.");
    var shifted = physicalOffset >> align;
    if ((ulong)shifted > OffsetMask)
      throw new InvalidOperationException("CSO/ZSO output exceeds the 31-bit shifted-index address space.");
    return checked((uint)shifted) | (flag ? Flag : 0);
  }

  private static long Alignment(byte align) {
    if (align > 30)
      throw new NotSupportedException($"CSO/ZSO index shift {align} is too large to maintain safely.");
    return 1L << align;
  }

  private static void PadToAlignment(Stream output, long alignment) {
    var padding = (-output.Position) & (alignment - 1);
    WriteZeros(output, padding);
  }

  private static void WriteZeros(Stream output, long count) {
    if (count <= 0) return;
    Span<byte> zeros = stackalloc byte[4096];
    while (count > 0) {
      var chunk = (int)Math.Min(count, zeros.Length);
      output.Write(zeros[..chunk]);
      count -= chunk;
    }
  }

  private static void CopyRange(Stream input, Stream output, long offset, long length) {
    if (length < 0)
      throw new InvalidDataException("CSO/ZSO block has a negative stored length.");
    input.Position = offset;
    var buffer = new byte[64 * 1024];
    var remaining = length;
    while (remaining > 0) {
      var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
      if (read <= 0) throw new EndOfStreamException("Unexpected end of CSO/ZSO block data.");
      output.Write(buffer, 0, read);
      remaining -= read;
    }
  }

  private static void ReadExactly(Stream stream, Span<byte> destination) {
    var read = 0;
    while (read < destination.Length) {
      var amount = stream.Read(destination[read..]);
      if (amount <= 0) throw new EndOfStreamException("Unexpected end of CSO/ZSO stream.");
      read += amount;
    }
  }

  private static FileStream CreateScratchStream()
    => new(Path.Combine(Path.GetTempPath(), "cwb_cso_" + Guid.NewGuid().ToString("N") + ".tmp"),
      FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
}
