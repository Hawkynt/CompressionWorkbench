#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Ogg;

/// <summary>
/// Parses a Vorbis / Opus comment block. Layout: vendor length + vendor string +
/// comment count + N × (comment length + UTF-8 string). Used by both Vorbis and
/// Opus streams identically — Vorbis prefixes the block with the packet-type byte
/// <c>0x03</c> + <c>"vorbis"</c>; Opus prefixes it with <c>"OpusTags"</c>. Callers
/// pass the block *after* stripping those prefixes.
/// </summary>
public sealed class VorbisCommentReader {
  public readonly record struct Parsed(string Vendor, IReadOnlyList<(string Key, string Value)> Comments);

  public Parsed Read(ReadOnlySpan<byte> body) {
    if (body.Length < 4) return new Parsed("", []);
    var vendorLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
    if (4 + vendorLen + 4 > body.Length) return new Parsed("", []);
    var vendor = Encoding.UTF8.GetString(body.Slice(4, vendorLen));
    var pos = 4 + vendorLen;
    var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
    pos += 4;

    var comments = new List<(string, string)>(count);
    for (var i = 0; i < count; ++i) {
      if (pos + 4 > body.Length) break;
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
      pos += 4;
      if (pos + len > body.Length) break;
      var entry = Encoding.UTF8.GetString(body.Slice(pos, len));
      pos += len;
      var eq = entry.IndexOf('=');
      if (eq < 0) comments.Add(("", entry));
      else comments.Add((entry[..eq], entry[(eq + 1)..]));
    }
    return new Parsed(vendor, comments);
  }
}
