#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Asf;

/// <summary>
/// Reassembles the per-stream elementary bitstreams carried by an ASF Data Object's
/// packets. This is the structural counterpart of FFmpeg's
/// <c>libavformat/asfdec_f.c</c> packet parser (<c>asf_get_packet</c> /
/// <c>asf_read_frame_header</c> / <c>asf_parse_packet</c>), implementing the ASF
/// Data Packet layout from the ASF specification:
/// <list type="bullet">
///   <item>an optional error-correction block (flag byte; the standard <c>0x82</c>
///     pattern carries two extra bytes);</item>
///   <item>a payload-parsing-information block with length-typed packet-length,
///     sequence and padding-length fields, the send time (32-bit) and duration
///     (16-bit), and — for multiple-payload packets — a payload count byte;</item>
///   <item>one or more payloads, each carrying a stream-number byte (top bit = key
///     frame), a length-typed media-object number, a length-typed offset-into-media-
///     object (or, for compressed payloads, a presentation time), and length-typed
///     replicated data. Replicated data ≥ 8 bytes means a normal fragment whose first
///     8 bytes are {media object size, presentation time}; replicated size == 1 marks
///     a <em>compressed</em> payload that itself contains several length-prefixed
///     sub-payloads.</item>
/// </list>
/// Fragments are stitched back together per media object (by stream and offset) and the
/// completed media objects are concatenated per stream in occurrence order. Parsing is
/// defensive: any inconsistency stops the walk and whatever reassembled cleanly so far
/// is returned.
/// </summary>
internal static class AsfDepayloader {

  /// <summary>One stream's reassembled elementary bitstream plus its completed-object boundaries.</summary>
  internal sealed class StreamData {
    public readonly List<byte[]> Objects = [];
    public byte[] ToBlob() {
      var total = 0;
      foreach (var o in this.Objects) total += o.Length;
      var blob = new byte[total];
      var p = 0;
      foreach (var o in this.Objects) { Array.Copy(o, 0, blob, p, o.Length); p += o.Length; }
      return blob;
    }
  }

  // In-progress reassembly buffer for one media object of one stream.
  private sealed class Pending {
    public int ObjectNumber = -1;
    public byte[] Buffer = [];
    public int FragOffset;
  }

  /// <summary>
  /// Depayloads <paramref name="packets"/> (the raw Data Object packet region) into a
  /// per-stream-number map of reassembled elementary streams. <paramref name="packetSize"/>
  /// is the fixed packet size (File Properties min == max); when it is positive packets
  /// are walked at that stride, otherwise the parser consumes packets back-to-back using
  /// each packet's declared length.
  /// </summary>
  internal static Dictionary<int, StreamData> Depayload(byte[] packets, int packetSize) {
    var streams = new Dictionary<int, StreamData>();
    var pending = new Dictionary<int, Pending>();
    try {
      var pos = 0;
      while (pos < packets.Length) {
        var consumed = ParsePacket(packets, pos, packetSize, streams, pending);
        if (consumed <= 0) break;
        pos += consumed;
      }
    } catch {
      // Defensive: keep whatever reassembled cleanly.
    }
    return streams;
  }

  private static int ParsePacket(byte[] b, int start, int packetSize,
      Dictionary<int, StreamData> streams, Dictionary<int, Pending> pending) {
    var p = start;
    var end = b.Length;
    if (p >= end) return 0;

    // ── error-correction block ───────────────────────────────────────────────
    var first = b[p];
    if ((first & 0x80) != 0) {
      // EC present: low nibble holds the EC data length (0x82 → 2 bytes EC data).
      var ecLen = first & 0x0F;
      p += 1 + ecLen;
    }
    if (p >= end) return 0;

    // ── payload parsing information ──────────────────────────────────────────
    var lengthTypeFlags = b[p++];
    var propertyFlags = b[p++];

    var multiplePayloads = (lengthTypeFlags & 0x01) != 0;
    var packetLenType = (lengthTypeFlags >> 5) & 3;
    var sequenceType = (lengthTypeFlags >> 1) & 3;
    var paddingType = (lengthTypeFlags >> 3) & 3;

    var packetLength = ReadLenTyped(b, ref p, packetLenType, (uint)(packetSize > 0 ? packetSize : 0));
    ReadLenTyped(b, ref p, sequenceType, 0);            // sequence (ignored)
    var padding = ReadLenTyped(b, ref p, paddingType, 0);

    if (p + 6 > end) return 0;
    p += 4; // send time
    p += 2; // duration

    int payloadCount;
    var payloadLenType = 0;
    if (multiplePayloads) {
      var pf = b[p++];
      payloadCount = pf & 0x3F;
      payloadLenType = (pf >> 6) & 3;
    } else {
      payloadCount = 1;
    }

    // Determine where this packet ends. With a fixed packet size we always advance one
    // full packet; otherwise honour the declared packet length (single-payload packets
    // without an explicit length run to the buffer end).
    int packetEnd;
    if (packetSize > 0) packetEnd = Math.Min(start + packetSize, end);
    else if (packetLength > 0) packetEnd = Math.Min(start + (int)packetLength, end);
    else packetEnd = end;

    var dataEnd = packetEnd - (int)padding;
    if (dataEnd > end) dataEnd = end;

    for (var pi = 0; pi < payloadCount; ++pi) {
      if (p >= dataEnd) break;
      if (!ParsePayload(b, ref p, dataEnd, propertyFlags, multiplePayloads, payloadLenType, streams, pending))
        break;
    }

    if (packetEnd <= start) return 0;
    return packetEnd - start;
  }

  private static bool ParsePayload(byte[] b, ref int p, int dataEnd, int propertyFlags,
      bool multiplePayloads, int payloadLenType,
      Dictionary<int, StreamData> streams, Dictionary<int, Pending> pending) {
    if (p >= dataEnd) return false;

    var streamByte = b[p++];
    var streamNumber = streamByte & 0x7F;
    // bit 7 = key frame (unused for reassembly)

    var mediaObjNumType = (propertyFlags >> 4) & 3;
    var offsetType = (propertyFlags >> 2) & 3;
    var replicatedType = propertyFlags & 3;

    var mediaObjectNumber = (int)ReadLenTyped(b, ref p, mediaObjNumType, 0);
    var offsetOrTime = (int)ReadLenTyped(b, ref p, offsetType, 0);
    var replicatedLength = (int)ReadLenTyped(b, ref p, replicatedType, 0);

    if (replicatedLength >= 8) {
      // Normal fragment: replicated data starts with {media object size, presentation time}.
      if (p + replicatedLength > dataEnd) return false;
      var mediaObjectSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
      p += replicatedLength; // skip the whole replicated-data block

      int fragLen;
      if (multiplePayloads) {
        fragLen = (int)ReadLenTyped(b, ref p, payloadLenType, 0);
      } else {
        fragLen = dataEnd - p;
      }
      if (fragLen < 0 || p + fragLen > dataEnd) return false;

      AppendFragment(streams, pending, streamNumber, mediaObjectNumber, mediaObjectSize, offsetOrTime, b, p, fragLen);
      p += fragLen;
      return true;
    }

    if (replicatedLength == 1) {
      // Compressed payload: 'offsetOrTime' was the presentation time; one presentation-
      // time-delta byte (the replicated byte), then a run of length-prefixed sub-payloads.
      var presentationTimeDelta = b[p++]; // the single replicated byte
      _ = presentationTimeDelta;

      int blockLen;
      if (multiplePayloads) {
        blockLen = (int)ReadLenTyped(b, ref p, payloadLenType, 0);
      } else {
        blockLen = dataEnd - p;
      }
      var blockEnd = p + blockLen;
      if (blockLen < 0 || blockEnd > dataEnd) return false;

      // Each sub-payload: 1-byte length prefix then that many bytes; each is a complete
      // media object for the stream.
      while (p < blockEnd) {
        var subLen = b[p++];
        if (p + subLen > blockEnd) return false;
        AppendCompleteObject(streams, streamNumber, b, p, subLen);
        p += subLen;
      }
      p = blockEnd;
      return true;
    }

    // replicatedLength == 0 (or other): treat the remaining payload as a single fragment.
    if (p + replicatedLength > dataEnd) return false;
    p += replicatedLength;
    int fl;
    if (multiplePayloads) {
      fl = (int)ReadLenTyped(b, ref p, payloadLenType, 0);
    } else {
      fl = dataEnd - p;
    }
    if (fl < 0 || p + fl > dataEnd) return false;
    AppendCompleteObject(streams, streamNumber, b, p, fl);
    p += fl;
    return true;
  }

  private static void AppendFragment(Dictionary<int, StreamData> streams, Dictionary<int, Pending> pending,
      int streamNumber, int objectNumber, int objectSize, int fragOffset, byte[] src, int srcPos, int len) {
    if (!pending.TryGetValue(streamNumber, out var pend) || pend.ObjectNumber != objectNumber || fragOffset == 0 && pend.FragOffset != 0) {
      // Finalise any complete previous object before starting a new one.
      if (pend != null && pend.FragOffset > 0 && pend.FragOffset == pend.Buffer.Length)
        Stream(streams, streamNumber).Objects.Add(pend.Buffer);
      pend = new Pending { ObjectNumber = objectNumber, Buffer = new byte[Math.Max(objectSize, fragOffset + len)], FragOffset = 0 };
      pending[streamNumber] = pend;
    }

    if (fragOffset + len > pend.Buffer.Length) {
      var grown = new byte[fragOffset + len];
      Array.Copy(pend.Buffer, grown, pend.Buffer.Length);
      pend.Buffer = grown;
    }
    Array.Copy(src, srcPos, pend.Buffer, fragOffset, len);
    pend.FragOffset = Math.Max(pend.FragOffset, fragOffset + len);

    if (pend.FragOffset >= pend.Buffer.Length) {
      Stream(streams, streamNumber).Objects.Add(pend.Buffer);
      pending.Remove(streamNumber);
    }
  }

  private static void AppendCompleteObject(Dictionary<int, StreamData> streams, int streamNumber, byte[] src, int srcPos, int len) {
    var obj = new byte[len];
    Array.Copy(src, srcPos, obj, 0, len);
    Stream(streams, streamNumber).Objects.Add(obj);
  }

  private static StreamData Stream(Dictionary<int, StreamData> streams, int n) {
    if (!streams.TryGetValue(n, out var s)) { s = new StreamData(); streams[n] = s; }
    return s;
  }

  // Length-typed field: 0 → default, 1 → u8, 2 → u16, 3 → u32 (little-endian).
  private static uint ReadLenTyped(byte[] b, ref int p, int type, uint defVal) {
    switch (type & 3) {
      case 1: return b[p++];
      case 2: { var v = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2; return v; }
      case 3: { var v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4; return v; }
      default: return defVal;
    }
  }
}
