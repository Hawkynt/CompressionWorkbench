#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Mp3;

/// <summary>
/// Parses an ID3v1 (/v1.1) tag — the fixed 128-byte trailer at the end of many
/// older MP3 files. The tag starts with ASCII <c>"TAG"</c>; fields are ISO-8859-1,
/// space-padded, typically NUL-terminated. ID3v1.1 cannibalises the last two bytes
/// of the comment field (28 bytes of text + 0x00 + 1-byte track number) which this
/// parser detects via the v1.1 sentinel.
/// </summary>
public sealed class Id3v1Reader {
  /// <summary>
  /// Parsed fields, or <c>null</c> if the file doesn't carry an ID3v1 tag.
  /// </summary>
  public sealed record Tag(string Title, string Artist, string Album, string Year,
                           string Comment, int? Track, byte GenreByte);

  public Tag? Read(ReadOnlySpan<byte> file) {
    if (file.Length < 128) return null;
    var tag = file[^128..];
    if (tag[0] != (byte)'T' || tag[1] != (byte)'A' || tag[2] != (byte)'G') return null;

    var title = ReadText(tag.Slice(3, 30));
    var artist = ReadText(tag.Slice(33, 30));
    var album = ReadText(tag.Slice(63, 30));
    var year = ReadText(tag.Slice(93, 4));

    // v1.1: byte 125 = 0x00 sentinel, byte 126 is track number.
    string comment;
    int? track = null;
    if (tag[125] == 0x00 && tag[126] != 0x00) {
      comment = ReadText(tag.Slice(97, 28));
      track = tag[126];
    } else {
      comment = ReadText(tag.Slice(97, 30));
    }
    var genreByte = tag[127];
    return new Tag(title, artist, album, year, comment, track, genreByte);
  }

  private static string ReadText(ReadOnlySpan<byte> field) {
    // Strings are space- or zero-padded. Trim both.
    var end = field.Length;
    while (end > 0 && (field[end - 1] == 0x00 || field[end - 1] == 0x20))
      --end;
    return Encoding.Latin1.GetString(field[..end]);
  }
}
