#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Vorbis;

/// <summary>
/// Ogg container page reader (RFC 3533). Walks a byte buffer and reassembles
/// logical packets across page boundaries. Vorbis packets are typically split
/// across several Ogg pages, so this reader joins 255-length segments until a
/// shorter segment signals the packet end.
/// </summary>
internal sealed class OggPageReader {
  public readonly record struct Packet(byte[] Data, uint Serial, long GranulePosition);

  /// <summary>
  /// Returns reassembled packets for the first logical bitstream in
  /// <paramref name="data"/>. Pages that belong to other serials are skipped.
  /// </summary>
  public static List<Packet> ReadPackets(byte[] data) {
    var result = new List<Packet>();
    uint? primarySerial = null;
    List<byte>? current = null;

    var pos = 0;
    var total = data.Length;
    while (pos + 27 <= total) {
      if (data[pos] != (byte)'O' || data[pos + 1] != (byte)'g' || data[pos + 2] != (byte)'g' || data[pos + 3] != (byte)'S')
        throw new InvalidDataException($"Ogg: missing 'OggS' page magic at offset {pos}.");
      var gp = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos + 6, 8));
      var serial = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 14, 4));
      var segCount = data[pos + 26];
      if (pos + 27 + segCount > total)
        throw new InvalidDataException("Ogg: segment table truncated.");
      var payloadOffset = pos + 27 + segCount;
      var payloadLen = 0;
      for (var i = 0; i < segCount; ++i) payloadLen += data[pos + 27 + i];
      if (payloadOffset + payloadLen > total)
        throw new InvalidDataException("Ogg: page payload truncated.");

      // Lock to the first logical bitstream we encounter.
      primarySerial ??= serial;
      if (serial != primarySerial.Value) {
        pos = payloadOffset + payloadLen;
        continue;
      }

      var segOff = payloadOffset;
      for (var i = 0; i < segCount; ++i) {
        var segLen = data[pos + 27 + i];
        current ??= new List<byte>(segLen + 32);
        if (segLen > 0) {
          var slice = new byte[segLen];
          Buffer.BlockCopy(data, segOff, slice, 0, segLen);
          current.AddRange(slice);
        }
        segOff += segLen;
        if (segLen < 255) {
          var packet = current.ToArray();
          current = null;
          result.Add(new Packet(packet, serial, gp));
        }
      }

      pos = payloadOffset + payloadLen;
    }

    if (current is { Count: > 0 })
      result.Add(new Packet(current.ToArray(), primarySerial ?? 0u, 0));

    return result;
  }
}
