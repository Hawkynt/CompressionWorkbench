#pragma warning disable CS1591

using System.Text;

namespace Codec.Speex;

/// <summary>
/// Minimal Ogg page walker for Speex logical streams (RFC 3533). Reassembles logical
/// packets across page boundaries using the segment-table lacing mechanism, exactly
/// like the sibling Ogg-Opus reader. The first packet is the Speex header, the second
/// is the Vorbis-comment block, and the remainder are audio packets.
/// </summary>
public sealed class OggSpeexReader {

  private static ReadOnlySpan<byte> OggS => "OggS"u8;

  private readonly Stream _stream;
  private readonly Queue<byte[]> _pending = new();
  private readonly List<byte> _partial = new();
  private bool _eof;

  /// <summary>
  /// Initializes a new instance of <see cref="OggSpeexReader"/>.
  /// </summary>
  public OggSpeexReader(Stream stream) => this._stream = stream;

  /// <summary>Reads and parses the first packet as the Speex header.</summary>
  public SpeexHeader ReadHeader() {
    if (!this.TryReadPacket(out var pkt))
      throw new InvalidDataException("Ogg Speex stream is empty (no header packet).");
    return SpeexHeader.Parse(pkt);
  }

  /// <summary>
  /// Reads the comment packet (the one following the header) if present, returning its
  /// raw bytes (a Vorbis-comment block); null when the stream has no further packet.
  /// </summary>
  public byte[]? TryReadComments() => this.TryReadPacket(out var pkt) ? pkt : null;

  /// <summary>
  /// Attempts to read the packet from the supplied input.
  /// </summary>
  public bool TryReadPacket(out byte[] packet) {
    while (this._pending.Count == 0 && !this._eof)
      this.FillFromNextPage();
    if (this._pending.Count > 0) {
      packet = this._pending.Dequeue();
      return true;
    }
    packet = [];
    return false;
  }

  private void FillFromNextPage() {
    Span<byte> header = stackalloc byte[27];
    if (!ReadExact(this._stream, header)) { this._eof = true; return; }
    if (!header[..4].SequenceEqual(OggS))
      throw new InvalidDataException("Not an Ogg stream: missing 'OggS' capture pattern.");

    int segmentCount = header[26];
    Span<byte> segments = stackalloc byte[segmentCount];
    if (segmentCount > 0 && !ReadExact(this._stream, segments)) { this._eof = true; return; }

    var totalBody = 0;
    for (var i = 0; i < segmentCount; ++i) totalBody += segments[i];

    var body = new byte[totalBody];
    if (totalBody > 0 && !ReadExact(this._stream, body)) { this._eof = true; return; }

    var cursor = 0;
    for (var i = 0; i < segmentCount; ++i) {
      var segLen = segments[i];
      this._partial.AddRange(body.AsSpan(cursor, segLen).ToArray());
      cursor += segLen;
      if (segLen < 255) {
        this._pending.Enqueue(this._partial.ToArray());
        this._partial.Clear();
      }
    }
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
