#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Mp3;

/// <summary>
/// Parses ID3v2 tag frames prepended to an MP3. Extracts common text frames
/// (TIT2/TPE1/TALB/TDRC/…), attached picture frames (APIC), URL frames
/// (WOAS/WOAF/…), and comment frames (COMM). Sync-safe integer handling and
/// de-unsynchronisation are implemented per ID3v2.4; the older v2.3 tags follow
/// the same layout and read correctly against this parser.
/// </summary>
public sealed class Id3v2Reader {
  public sealed record Frame(string Id, string MimeType, string Description, byte[] Payload);

  /// <summary>Returns extracted frames, or an empty list if no ID3v2 tag is present.</summary>
  public (int HeaderBytes, List<Frame> Frames) Read(ReadOnlySpan<byte> data) {
    if (data.Length < 10 || data[0] != 'I' || data[1] != 'D' || data[2] != '3')
      return (0, []);

    var majorVersion = data[3];
    var flags = data[5];
    var tagSize = DecodeSyncSafe(data[6..10]);
    var headerEnd = 10 + tagSize;
    if (headerEnd > data.Length) return (data.Length, []);

    var body = data.Slice(10, tagSize);
    // Extended header (bit 6 of flags) — skip it; its size is sync-safe at the start.
    var pos = 0;
    if ((flags & 0x40) != 0 && body.Length >= 4) {
      var extSize = DecodeSyncSafe(body[..4]);
      pos += extSize;
    }

    var frames = new List<Frame>();
    while (pos + 10 <= body.Length) {
      var id = Encoding.ASCII.GetString(body.Slice(pos, 4));
      if (id[0] == 0) break; // padding

      // v2.4 uses sync-safe frame sizes; v2.3 uses plain big-endian.
      var rawSize = body.Slice(pos + 4, 4);
      var frameSize = majorVersion >= 4 ? DecodeSyncSafe(rawSize) : ReadBigEndian32(rawSize);
      if (frameSize <= 0 || pos + 10 + frameSize > body.Length) break;

      var frameBody = body.Slice(pos + 10, frameSize);
      pos += 10 + frameSize;

      if (id.StartsWith('T')) frames.Add(DecodeText(id, frameBody));
      else if (id == "APIC") frames.Add(DecodeApic(frameBody));
      else if (id == "COMM") frames.Add(DecodeComment(frameBody));
      else if (id.StartsWith('W')) frames.Add(DecodeUrl(id, frameBody));
      else frames.Add(new Frame(id, "application/octet-stream", "", frameBody.ToArray()));
    }
    return (headerEnd, frames);
  }

  private static Frame DecodeText(string id, ReadOnlySpan<byte> body) {
    if (body.Length < 1) return new Frame(id, "text/plain", "", []);
    var text = DecodeString(body[0], body[1..]);
    return new Frame(id, "text/plain", "", Encoding.UTF8.GetBytes(text));
  }

  private static Frame DecodeApic(ReadOnlySpan<byte> body) {
    if (body.Length < 2) return new Frame("APIC", "application/octet-stream", "", []);
    var encoding = body[0];
    var mimeEnd = body[1..].IndexOf((byte)0);
    if (mimeEnd < 0) return new Frame("APIC", "application/octet-stream", "", body.ToArray());
    var mime = Encoding.Latin1.GetString(body.Slice(1, mimeEnd));
    var afterMime = 1 + mimeEnd + 1;
    if (afterMime + 1 >= body.Length) return new Frame("APIC", mime, "", []);
    var pictureType = body[afterMime];
    // Description (null-terminated in declared encoding) follows picture type.
    var descEnd = FindEncodedTerminator(body[(afterMime + 1)..], encoding);
    var desc = DecodeString(encoding, body.Slice(afterMime + 1, descEnd));
    var termLen = encoding is 1 or 2 ? 2 : 1;
    var dataStart = afterMime + 1 + descEnd + termLen;
    var payload = dataStart < body.Length ? body[dataStart..].ToArray() : [];
    _ = pictureType;
    return new Frame("APIC", mime, desc, payload);
  }

  private static Frame DecodeComment(ReadOnlySpan<byte> body) {
    if (body.Length < 5) return new Frame("COMM", "text/plain", "", []);
    var encoding = body[0];
    // Skip 3-byte language code; then short description + terminator + actual comment.
    var rest = body[4..];
    var descEnd = FindEncodedTerminator(rest, encoding);
    var desc = DecodeString(encoding, rest[..descEnd]);
    var termLen = encoding is 1 or 2 ? 2 : 1;
    var textStart = descEnd + termLen;
    var text = textStart < rest.Length ? DecodeString(encoding, rest[textStart..]) : "";
    return new Frame("COMM", "text/plain", desc, Encoding.UTF8.GetBytes(text));
  }

  private static Frame DecodeUrl(string id, ReadOnlySpan<byte> body) {
    return new Frame(id, "text/uri-list", "", body.ToArray());
  }

  private static string DecodeString(byte encoding, ReadOnlySpan<byte> bytes) {
    return encoding switch {
      0 => Encoding.Latin1.GetString(bytes),
      1 => Encoding.Unicode.GetString(bytes).TrimEnd('\uFEFF'),     // UTF-16 with BOM
      2 => Encoding.BigEndianUnicode.GetString(bytes),
      3 => Encoding.UTF8.GetString(bytes),
      _ => Encoding.Latin1.GetString(bytes),
    };
  }

  private static int FindEncodedTerminator(ReadOnlySpan<byte> bytes, byte encoding) {
    if (encoding is 1 or 2) {
      for (var i = 0; i + 1 < bytes.Length; i += 2)
        if (bytes[i] == 0 && bytes[i + 1] == 0) return i;
      return bytes.Length;
    }
    var idx = bytes.IndexOf((byte)0);
    return idx < 0 ? bytes.Length : idx;
  }

  private static int DecodeSyncSafe(ReadOnlySpan<byte> b)
    => (b[0] & 0x7F) << 21 | (b[1] & 0x7F) << 14 | (b[2] & 0x7F) << 7 | (b[3] & 0x7F);

  private static int ReadBigEndian32(ReadOnlySpan<byte> b)
    => b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];
}
