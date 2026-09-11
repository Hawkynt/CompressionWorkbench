using System.Buffers.Binary;

namespace FileFormat.Nrg;

/// <summary>Minimal descriptor-chain inspection shared by mutation/maintenance profile guards.</summary>
internal static class NrgStructureInspector {
  internal readonly record struct Profile(int TrackCount, byte? SingleTrackMode) {
    internal bool IsSingleDataTrack => this.TrackCount == 1 && this.SingleTrackMode is
      0x00 or 0x02 or 0x03 or 0x05 or 0x06 or 0x0F or 0x11;
  }

  internal static Profile Inspect(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("NRG inspection requires a readable, seekable stream.", nameof(stream));

    var saved = stream.Position;
    try {
      if (!TryGetDescriptorBounds(stream, out var descriptorOffset, out var footerOffset))
        return default;

      var trackCount = 0;
      byte? singleMode = null;
      var position = descriptorOffset;
      Span<byte> header = stackalloc byte[8];
      while (position <= footerOffset - header.Length) {
        stream.Position = position;
        if (!ReadExactly(stream, header))
          break;

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        var payloadStart = position + header.Length;
        if (payloadStart > footerOffset || payloadLength > (ulong)(footerOffset - payloadStart))
          break;

        if (header[..4].SequenceEqual("DAOX"u8))
          CountDao(stream, payloadStart, payloadLength, 42, ref trackCount, ref singleMode);
        else if (header[..4].SequenceEqual("DAOI"u8))
          CountDao(stream, payloadStart, payloadLength, 30, ref trackCount, ref singleMode);
        else if (header[..4].SequenceEqual("ETN2"u8))
          CountEtn(stream, payloadStart, payloadLength, 32, modeOffset: 19, ref trackCount, ref singleMode);
        else if (header[..4].SequenceEqual("ETNF"u8))
          CountEtn(stream, payloadStart, payloadLength, 20, modeOffset: 11, ref trackCount, ref singleMode);

        position = payloadStart + payloadLength;
        if (header[..4].SequenceEqual("END!"u8))
          break;
      }

      return new(trackCount, trackCount == 1 ? singleMode : null);
    } finally {
      stream.Position = saved;
    }
  }

  private static void CountDao(Stream stream, long payloadStart, uint payloadLength, int recordSize,
      ref int trackCount, ref byte? singleMode) {
    const int headerSize = 22;
    if (payloadLength < headerSize)
      return;
    var records = checked((int)((payloadLength - headerSize) / recordSize));
    for (var index = 0; index < records; ++index) {
      stream.Position = payloadStart + headerSize + (long)index * recordSize + 14;
      var mode = stream.ReadByte();
      if (mode < 0)
        return;
      AddMode((byte)mode, ref trackCount, ref singleMode);
    }
  }

  private static void CountEtn(Stream stream, long payloadStart, uint payloadLength, int recordSize, int modeOffset,
      ref int trackCount, ref byte? singleMode) {
    var records = checked((int)(payloadLength / recordSize));
    for (var index = 0; index < records; ++index) {
      stream.Position = payloadStart + (long)index * recordSize + modeOffset;
      var mode = stream.ReadByte();
      if (mode < 0)
        return;
      AddMode((byte)mode, ref trackCount, ref singleMode);
    }
  }

  private static void AddMode(byte mode, ref int trackCount, ref byte? singleMode) {
    ++trackCount;
    singleMode = trackCount == 1 ? mode : null;
  }

  private static bool TryGetDescriptorBounds(Stream stream, out long descriptorOffset, out long footerOffset) {
    descriptorOffset = 0;
    footerOffset = 0;

    if (stream.Length >= 12) {
      stream.Position = stream.Length - 12;
      Span<byte> footer = stackalloc byte[12];
      if (ReadExactly(stream, footer) && footer[..4].SequenceEqual("NER5"u8)) {
        var offset = BinaryPrimitives.ReadUInt64BigEndian(footer[4..]);
        footerOffset = stream.Length - 12;
        if (offset <= (ulong)footerOffset) {
          descriptorOffset = checked((long)offset);
          return true;
        }
      }
    }

    if (stream.Length >= 8) {
      stream.Position = stream.Length - 8;
      Span<byte> footer = stackalloc byte[8];
      if (ReadExactly(stream, footer) && footer[..4].SequenceEqual("NERO"u8)) {
        var offset = BinaryPrimitives.ReadUInt32BigEndian(footer[4..]);
        footerOffset = stream.Length - 8;
        if (offset <= footerOffset) {
          descriptorOffset = offset;
          return true;
        }
      }
    }

    return false;
  }

  private static bool ReadExactly(Stream stream, Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = stream.Read(buffer[offset..]);
      if (read == 0)
        return false;
      offset += read;
    }
    return true;
  }
}
