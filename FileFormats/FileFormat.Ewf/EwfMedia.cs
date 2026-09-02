#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Checksums;
using FileFormat.Zlib;

namespace FileFormat.Ewf;

/// <summary>
/// Reconstructs the acquired medium represented by a single-segment EWF image.
/// The descriptor treats that medium as the one semantic mutable payload; EWF
/// sections remain diagnostic/internal views because editing one independently
/// would invalidate table offsets, checksums and evidence hashes.
/// </summary>
internal static class EwfMedia {
  private const int TableHeaderSize = 24;

  public static bool TryExtract(EwfReader.EwfImage image, out byte[] media) {
    try {
      media = Extract(image);
      return true;
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or OverflowException) {
      media = [];
      return false;
    }
  }

  public static byte[] Extract(EwfReader.EwfImage image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.IsLogical)
      throw new NotSupportedException("Logical EWF/L01 media reconstruction is not implemented.");

    var volume = image.Sections.FirstOrDefault(s => s.Type is "volume" or "data")
      ?? throw new InvalidDataException("EWF image has no volume/data media descriptor.");
    if (volume.Payload.Length < 20)
      throw new InvalidDataException("EWF volume payload is too short.");

    var chunkCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(volume.Payload.AsSpan(4, 4)));
    var sectorsPerChunk = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(volume.Payload.AsSpan(8, 4)));
    var bytesPerSector = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(volume.Payload.AsSpan(12, 4)));
    var totalSectors = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(volume.Payload.AsSpan(16, 4)));
    if (chunkCount < 0 || sectorsPerChunk <= 0 || bytesPerSector <= 0 || totalSectors < 0)
      throw new InvalidDataException("EWF volume geometry is invalid.");

    var sectors = image.Sections.FirstOrDefault(s => s.Type == "sectors")
      ?? throw new InvalidDataException("EWF image has no sectors section.");
    var table = image.Sections.FirstOrDefault(s => s.Type is "table" or "table2")
      ?? throw new InvalidDataException("EWF image has no chunk table.");
    var entries = ReadTable(table.Payload, chunkCount);

    var expectedBytes = checked((long)totalSectors * bytesPerSector);
    if (expectedBytes > int.MaxValue)
      throw new NotSupportedException("EWF media exceeds the in-memory mutation profile.");
    var output = new byte[(int)expectedBytes];
    var outputOffset = 0;
    var nominalChunkBytes = checked(sectorsPerChunk * bytesPerSector);

    for (var i = 0; i < entries.Length && outputOffset < output.Length; ++i) {
      var encoded = entries[i];
      var compressed = (encoded & 0x80000000u) != 0;
      var relative = encoded & 0x7FFFFFFFu;
      var nextRelative = i + 1 < entries.Length
        ? entries[i + 1] & 0x7FFFFFFFu
        : checked((uint)(EwfReader.SectionDescriptorSize + sectors.Payload.Length));
      if (relative < EwfReader.SectionDescriptorSize || nextRelative < relative)
        throw new InvalidDataException("EWF chunk table contains invalid offsets.");

      var start = checked((int)relative - EwfReader.SectionDescriptorSize);
      var end = checked((int)nextRelative - EwfReader.SectionDescriptorSize);
      if (start < 0 || end < start || end > sectors.Payload.Length)
        throw new InvalidDataException("EWF chunk table points outside the sectors payload.");
      var stored = sectors.Payload.AsSpan(start, end - start);

      byte[] chunk;
      if (compressed) {
        chunk = ZlibStream.Decompress(stored);
      } else {
        if (stored.Length < 4)
          throw new InvalidDataException("Stored EWF chunk is shorter than its Adler-32 trailer.");
        var data = stored[..^4];
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(stored[^4..]);
        var actual = Adler32.Compute(data);
        if (actual != expected)
          throw new InvalidDataException(
            $"Stored EWF chunk Adler-32 mismatch: expected 0x{expected:X8}, got 0x{actual:X8}.");
        chunk = data.ToArray();
      }

      var expectedChunk = Math.Min(nominalChunkBytes, output.Length - outputOffset);
      if (chunk.Length < expectedChunk)
        throw new InvalidDataException("EWF chunk decompressed shorter than the declared media geometry.");
      chunk.AsSpan(0, expectedChunk).CopyTo(output.AsSpan(outputOffset));
      outputOffset += expectedChunk;
    }

    if (outputOffset != output.Length)
      throw new InvalidDataException("EWF chunk table does not cover the complete declared medium.");
    return output;
  }

  private static uint[] ReadTable(byte[] payload, int expectedChunks) {
    if (payload.Length < TableHeaderSize + 4)
      throw new InvalidDataException("EWF table payload is too short.");
    var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)));
    if (count != expectedChunks)
      throw new InvalidDataException($"EWF table has {count} chunks, volume declares {expectedChunks}.");
    var entriesBytes = checked(count * 4);
    if (TableHeaderSize + entriesBytes + 4 > payload.Length)
      throw new InvalidDataException("EWF table entry array is truncated.");

    var entries = new uint[count];
    for (var i = 0; i < count; ++i)
      entries[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(TableHeaderSize + i * 4, 4));
    return entries;
  }
}