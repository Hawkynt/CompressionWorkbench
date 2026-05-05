#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Text;

namespace Codec.Opus;

/// <summary>
/// <c>OpusHead</c> identification-header packet contents per RFC 7845 §5.1.
/// </summary>
public sealed record OpusHeadPacket(
  byte Version,
  byte ChannelCount,
  ushort PreSkip,
  uint InputSampleRate,
  short OutputGainQ8,
  byte ChannelMappingFamily);

/// <summary>
/// <c>OpusTags</c> comment-header packet contents per RFC 7845 §5.2.
/// </summary>
public sealed record OpusTagsPacket(string Vendor, IReadOnlyList<string> Comments);

/// <summary>
/// Minimal Ogg page walker specialised for Opus streams (RFC 7845 / RFC 3533).
/// Reassembles logical packets across page boundaries using the segment-table
/// "lacing" mechanism. Does not verify CRCs (we trust the stream here).
/// </summary>
public sealed class OggOpusReader {

  private static ReadOnlySpan<byte> OggS => "OggS"u8;
  private static ReadOnlySpan<byte> HeadMagic => "OpusHead"u8;
  private static ReadOnlySpan<byte> TagsMagic => "OpusTags"u8;

  private readonly Stream _stream;
  private readonly Queue<byte[]> _pendingPackets = new();
  private readonly List<byte> _partial = new();
  private bool _eof;
  private OpusHeadPacket? _head;
  private bool _readHead;

  public OggOpusReader(Stream stream) => this._stream = stream;

  /// <summary>
  /// Reads and validates the first logical packet, which must be <c>OpusHead</c>.
  /// </summary>
  public OpusHeadPacket ReadHead() {
    if (this._readHead) return this._head!;
    this._readHead = true;

    if (!this.TryReadPacket(out var pkt))
      throw new InvalidDataException("Ogg Opus stream is empty (no OpusHead packet).");

    if (pkt.Length < 19 || !pkt.AsSpan(0, 8).SequenceEqual(HeadMagic))
      throw new InvalidDataException("Ogg Opus stream missing 'OpusHead' magic in first packet.");

    var version = pkt[8];
    var channels = pkt[9];
    var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(pkt.AsSpan(10, 2));
    var inputRate = BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(12, 4));
    var gain = BinaryPrimitives.ReadInt16LittleEndian(pkt.AsSpan(16, 2));
    var mapping = pkt[18];

    this._head = new OpusHeadPacket(version, channels, preSkip, inputRate, gain, mapping);
    return this._head;
  }

  /// <summary>
  /// Reads the second logical packet if it is <c>OpusTags</c>, otherwise buffers
  /// it back for audio consumption and returns null.
  /// </summary>
  public OpusTagsPacket? TryReadTags() {
    if (!this._readHead) this.ReadHead();
    if (!this.TryReadPacket(out var pkt)) return null;

    if (pkt.Length < 8 || !pkt.AsSpan(0, 8).SequenceEqual(TagsMagic)) {
      // Not a tags packet — push it back to the front of the queue.
      var rebuilt = new Queue<byte[]>();
      rebuilt.Enqueue(pkt);
      foreach (var p in this._pendingPackets) rebuilt.Enqueue(p);
      this._pendingPackets.Clear();
      foreach (var p in rebuilt) this._pendingPackets.Enqueue(p);
      return null;
    }

    var pos = 8;
    if (pos + 4 > pkt.Length) return new OpusTagsPacket(string.Empty, Array.Empty<string>());
    var vendorLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(pos, 4));
    pos += 4;
    if (vendorLen < 0 || pos + vendorLen > pkt.Length)
      return new OpusTagsPacket(string.Empty, Array.Empty<string>());
    var vendor = Encoding.UTF8.GetString(pkt, pos, vendorLen);
    pos += vendorLen;

    var comments = new List<string>();
    if (pos + 4 <= pkt.Length) {
      var listLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(pos, 4));
      pos += 4;
      for (var i = 0; i < listLen && pos + 4 <= pkt.Length; i++) {
        var cLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(pos, 4));
        pos += 4;
        if (cLen < 0 || pos + cLen > pkt.Length) break;
        comments.Add(Encoding.UTF8.GetString(pkt, pos, cLen));
        pos += cLen;
      }
    }

    return new OpusTagsPacket(vendor, comments);
  }

  /// <summary>
  /// Pulls the next reassembled logical packet from the Ogg stream.
  /// </summary>
  public bool TryReadPacket(out byte[] packet) {
    while (this._pendingPackets.Count == 0 && !this._eof)
      this.FillFromNextPage();

    if (this._pendingPackets.Count > 0) {
      packet = this._pendingPackets.Dequeue();
      return true;
    }
    packet = Array.Empty<byte>();
    return false;
  }

  private void FillFromNextPage() {
    Span<byte> header = stackalloc byte[27];
    if (!ReadExact(this._stream, header)) { this._eof = true; return; }
    if (!header[..4].SequenceEqual(OggS))
      throw new InvalidDataException("Not an Ogg stream: missing 'OggS' capture pattern.");

    // header[4] = version, [5] = header-type flags, [6..14] = granule,
    // [14..18] = serial, [18..22] = sequence, [22..26] = crc, [26] = segments
    int segmentCount = header[26];
    Span<byte> segments = stackalloc byte[segmentCount];
    if (segmentCount > 0 && !ReadExact(this._stream, segments)) { this._eof = true; return; }

    var totalBody = 0;
    for (var i = 0; i < segmentCount; i++) totalBody += segments[i];

    var body = new byte[totalBody];
    if (totalBody > 0 && !ReadExact(this._stream, body)) { this._eof = true; return; }

    // Walk segments: every segment <255 ends a packet; runs of 255s continue it.
    var cursor = 0;
    for (var i = 0; i < segmentCount; i++) {
      var segLen = segments[i];
      this._partial.AddRange(body.AsSpan(cursor, segLen).ToArray());
      cursor += segLen;
      if (segLen < 255) {
        this._pendingPackets.Enqueue(this._partial.ToArray());
        this._partial.Clear();
      }
    }
    // A trailing run of 255s leaves _partial non-empty; it will continue on the
    // next page (by Ogg design).
  }

  private static bool ReadExact(Stream stream, Span<byte> buf) {
    var total = 0;
    while (total < buf.Length) {
      var n = stream.Read(buf[total..]);
      if (n <= 0) return false;
      total += n;
    }
    return true;
  }
}
