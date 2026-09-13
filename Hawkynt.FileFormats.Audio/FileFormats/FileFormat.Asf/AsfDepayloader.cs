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
/// Fragments are stitched back together per media object (by stream and offset). The
/// completed media-object boundaries, presentation timestamps and key-frame flags are
/// retained for lossless remuxing, while <see cref="StreamData.ToBlob"/> still provides
/// the historical concatenated elementary-stream view. Parsing is defensive: any
/// inconsistency stops the walk and whatever reassembled cleanly so far is returned.
/// </summary>
internal static class AsfDepayloader {

  internal sealed record MediaObject(byte[] Data, uint PresentationTimeMs, bool KeyFrame);

  /// <summary>One stream's reassembled elementary bitstream plus its completed-object boundaries.</summary>
  internal sealed class StreamData {
    public readonly List<MediaObject> Objects = [];

    public byte[] ToBlob() {
      var total = 0;
      foreach (var o in this.Objects)
        total = checked(total + o.Data.Length);
      var blob = new byte[total];
      var p = 0;
      foreach (var o in this.Objects) {
        Array.Copy(o.Data, 0, blob, p, o.Data.Length);
        p += o.Data.Length;
      }
      return blob;
    }
  }

  // In-progress reassembly buffer for one media object of one stream.
  private sealed class Pending {
    public int ObjectNumber = -1;
    public byte[] Buffer = [];
    public int FragOffset;
    public uint PresentationTimeMs;
    public bool KeyFrame;
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
        if (consumed <= 0)
          break;
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
    if (p >= end)
      return 0;

    // ── error-correction block ───────────────────────────────────────────────
    var first = b[p];
    if ((first & 0x80) != 0) {
      // EC present: low nibble holds the EC data length (0x82 → 2 bytes EC data).
      var ecLen = first & 0x0F;
      p += 1 + ecLen;
    }
    if (p >= end)
      return 0;

    // ── payload parsing information ──────────────────────────────────────────
    var lengthTypeFlags = b[p++];
    var propertyFlags = b[p++];

    var multiplePayloads = (lengthTypeFlags & 0x01) != 0;
    var packetLenType = (lengthTypeFlags >> 5) & 3;
    var sequenceType = (lengthTypeFlags >> 1) & 3;
    var paddingType = (lengthTypeFlags >> 3) & 3;

    var packetLength = ReadLenTyped(b, ref p, packetLenType, (uint)(packetSize > 0 ? packetSize : 0));
    ReadLenTyped(b, ref p, sequenceType, 0); // sequence (ignored)
    var padding = ReadLenTyped(b, ref p, paddingType, 0);

    if (p + 6 > end)
      return 0;
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

    int packetEnd;
    if (packetSize > 0)
      packetEnd = Math.Min(start + packetSize, end);
    else if (packetLength > 0)
      packetEnd = Math.Min(start + checked((int)packetLength), end);
    else
      packetEnd = end;

    var dataEnd = packetEnd - checked((int)padding);
    if (dataEnd > end)
      dataEnd = end;
    if (dataEnd < p)
      return packetEnd > start ? packetEnd - start : 0;

    for (var pi = 0; pi < payloadCount; ++pi) {
      if (p >= dataEnd)
        break;
      if (!ParsePayload(b, ref p, dataEnd, propertyFlags, multiplePayloads, payloadLenType, streams, pending))
        break;
    }

    return packetEnd > start ? packetEnd - start : 0;
  }

  private static bool ParsePayload(byte[] b, ref int p, int dataEnd, int propertyFlags,
      bool multiplePayloads, int payloadLenType,
      Dictionary<int, StreamData> streams, Dictionary<int, Pending> pending) {
    if (p >= dataEnd)
      return false;

    var streamByte = b[p++];
    var streamNumber = streamByte & 0x7F;
    var keyFrame = (streamByte & 0x80) != 0;

    var mediaObjNumType = (propertyFlags >> 4) & 3;
    var offsetType = (propertyFlags >> 2) & 3;
    var replicatedType = propertyFlags & 3;

    var mediaObjectNumberRaw = ReadLenTyped(b, ref p, mediaObjNumType, 0);
    var offsetOrTimeRaw = ReadLenTyped(b, ref p, offsetType, 0);
    var replicatedLength = checked((int)ReadLenTyped(b, ref p, replicatedType, 0));
    if (mediaObjectNumberRaw > int.MaxValue || offsetOrTimeRaw > int.MaxValue)
      return false;
    var mediaObjectNumber = (int)mediaObjectNumberRaw;
    var offsetOrTime = (int)offsetOrTimeRaw;

    if (replicatedLength >= 8) {
      if (p + replicatedLength > dataEnd)
        return false;
      var objectSizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
      if (objectSizeRaw > int.MaxValue)
        return false;
      var mediaObjectSize = (int)objectSizeRaw;
      var presentationTimeMs = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4));
      p += replicatedLength;

      int fragLen;
      if (multiplePayloads)
        fragLen = checked((int)ReadLenTyped(b, ref p, payloadLenType, 0));
      else
        fragLen = dataEnd - p;
      if (fragLen < 0 || p + fragLen > dataEnd)
        return false;

      AppendFragment(streams, pending, streamNumber, mediaObjectNumber, mediaObjectSize,
        offsetOrTime, presentationTimeMs, keyFrame, b, p, fragLen);
      p += fragLen;
      return true;
    }

    if (replicatedLength == 1) {
      if (p >= dataEnd)
        return false;
      var presentationTimeDelta = b[p++];

      int blockLen;
      if (multiplePayloads)
        blockLen = checked((int)ReadLenTyped(b, ref p, payloadLenType, 0));
      else
        blockLen = dataEnd - p;
      var blockEnd = p + blockLen;
      if (blockLen < 0 || blockEnd > dataEnd)
        return false;

      var presentationTimeMs = unchecked((uint)offsetOrTime);
      while (p < blockEnd) {
        var subLen = b[p++];
        if (p + subLen > blockEnd)
          return false;
        AppendCompleteObject(streams, streamNumber, b, p, subLen, presentationTimeMs, keyFrame);
        p += subLen;
        presentationTimeMs = unchecked(presentationTimeMs + presentationTimeDelta);
      }
      p = blockEnd;
      return true;
    }

    if (p + replicatedLength > dataEnd)
      return false;
    p += replicatedLength;
    int len;
    if (multiplePayloads)
      len = checked((int)ReadLenTyped(b, ref p, payloadLenType, 0));
    else
      len = dataEnd - p;
    if (len < 0 || p + len > dataEnd)
      return false;
    AppendCompleteObject(streams, streamNumber, b, p, len, 0, keyFrame);
    p += len;
    return true;
  }

  private static void AppendFragment(
      Dictionary<int, StreamData> streams,
      Dictionary<int, Pending> pending,
      int streamNumber,
      int objectNumber,
      int objectSize,
      int fragOffset,
      uint presentationTimeMs,
      bool keyFrame,
      byte[] src,
      int srcPos,
      int len) {
    if (fragOffset < 0 || objectSize < 0 || fragOffset > int.MaxValue - len)
      return;

    if (!pending.TryGetValue(streamNumber, out var pend) || pend.ObjectNumber != objectNumber || fragOffset == 0 && pend.FragOffset != 0) {
      pend = new Pending {
        ObjectNumber = objectNumber,
        Buffer = new byte[Math.Max(objectSize, fragOffset + len)],
        FragOffset = 0,
        PresentationTimeMs = presentationTimeMs,
        KeyFrame = keyFrame,
      };
      pending[streamNumber] = pend;
    } else {
      pend.KeyFrame |= keyFrame;
    }

    if (fragOffset + len > pend.Buffer.Length) {
      var grown = new byte[fragOffset + len];
      Array.Copy(pend.Buffer, grown, pend.Buffer.Length);
      pend.Buffer = grown;
    }
    Array.Copy(src, srcPos, pend.Buffer, fragOffset, len);
    pend.FragOffset = Math.Max(pend.FragOffset, fragOffset + len);

    if (pend.FragOffset >= pend.Buffer.Length) {
      Stream(streams, streamNumber).Objects.Add(new MediaObject(pend.Buffer, pend.PresentationTimeMs, pend.KeyFrame));
      pending.Remove(streamNumber);
    }
  }

  private static void AppendCompleteObject(
      Dictionary<int, StreamData> streams,
      int streamNumber,
      byte[] src,
      int srcPos,
      int len,
      uint presentationTimeMs,
      bool keyFrame) {
    var obj = new byte[len];
    Array.Copy(src, srcPos, obj, 0, len);
    Stream(streams, streamNumber).Objects.Add(new MediaObject(obj, presentationTimeMs, keyFrame));
  }

  private static StreamData Stream(Dictionary<int, StreamData> streams, int streamNumber) {
    if (!streams.TryGetValue(streamNumber, out var stream)) {
      stream = new StreamData();
      streams[streamNumber] = stream;
    }
    return stream;
  }

  // Length-typed field: 0 → default, 1 → u8, 2 → u16, 3 → u32 (little-endian).
  private static uint ReadLenTyped(byte[] b, ref int p, int type, uint defaultValue) {
    switch (type & 3) {
      case 1:
        return b[p++];
      case 2: {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
        p += 2;
        return value;
      }
      case 3: {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
        p += 4;
        return value;
      }
      default:
        return defaultValue;
    }
  }
}
