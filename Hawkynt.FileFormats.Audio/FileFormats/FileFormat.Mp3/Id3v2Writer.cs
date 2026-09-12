#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Mp3;

/// <summary>
/// Emits an ID3v2.4 tag. Supports text frames (TIT2, TPE1, TALB, TDRC, TCON,
/// TRCK, TCOM, TPUB, COMM), URL frames (WOAF, WORS, WOAS, …), APIC picture
/// frames with MIME auto-detection (JPEG / PNG), and USLT lyric frames.
/// <para>
/// Format: the archive-view of an MP3 reads cover.jpg / metadata.ini; this
/// writer takes the same convention and produces an ID3v2 tag blob that the
/// Mp3FormatDescriptor prepends to the audio stream on Create.
/// </para>
/// </summary>
public sealed class Id3v2Writer {
  private readonly List<(string Id, byte[] Body)> _frames = [];

  /// <summary>Adds a text frame (TIT2, TPE1, TALB, …). Text is written as UTF-8.</summary>
  public void AddText(string frameId, string text) {
    if (frameId.Length != 4) throw new ArgumentException("Frame ID must be 4 chars.", nameof(frameId));
    var body = new byte[1 + Encoding.UTF8.GetByteCount(text)];
    body[0] = 0x03;  // encoding: UTF-8
    Encoding.UTF8.GetBytes(text).CopyTo(body, 1);
    this._frames.Add((frameId, body));
  }

  /// <summary>Adds a URL frame (WOAF, WORS, …). Per the spec URL frames have no encoding byte.</summary>
  public void AddUrl(string frameId, string url) {
    if (frameId.Length != 4 || frameId[0] != 'W')
      throw new ArgumentException("URL frame IDs begin with 'W'.", nameof(frameId));
    this._frames.Add((frameId, Encoding.UTF8.GetBytes(url)));
  }

  /// <summary>
  /// Adds an APIC picture frame. MIME type is auto-detected from the first bytes
  /// of <paramref name="pictureBytes"/> (JPEG <c>FF D8 FF</c>; PNG <c>89 50 4E 47</c>;
  /// GIF <c>47 49 46 38</c>). <paramref name="description"/> is a human-readable label.
  /// </summary>
  public void AddPicture(byte[] pictureBytes, string description = "Cover", byte pictureType = 0x03) {
    var mime = DetectMime(pictureBytes);
    var mimeBytes = Encoding.ASCII.GetBytes(mime);
    var descBytes = Encoding.UTF8.GetBytes(description);

    using var ms = new MemoryStream();
    ms.WriteByte(0x03);                  // encoding: UTF-8
    ms.Write(mimeBytes);
    ms.WriteByte(0x00);                  // MIME terminator
    ms.WriteByte(pictureType);           // picture type (3 = cover front)
    ms.Write(descBytes);
    ms.WriteByte(0x00);                  // description terminator
    ms.Write(pictureBytes);
    this._frames.Add(("APIC", ms.ToArray()));
  }

  /// <summary>
  /// Adds an unsynchronised lyric/text transcription (USLT) frame.
  /// </summary>
  public void AddLyrics(string lyrics, string language = "eng", string description = "") {
    var langBytes = Encoding.ASCII.GetBytes(language.PadRight(3)[..3]);
    var descBytes = Encoding.UTF8.GetBytes(description);
    var textBytes = Encoding.UTF8.GetBytes(lyrics);

    using var ms = new MemoryStream();
    ms.WriteByte(0x03);
    ms.Write(langBytes);
    ms.Write(descBytes);
    ms.WriteByte(0x00);
    ms.Write(textBytes);
    this._frames.Add(("USLT", ms.ToArray()));
  }

  /// <summary>
  /// Emits the complete ID3v2.4 tag bytes, ready to be prepended to the audio
  /// frames of an MP3 file.
  /// </summary>
  public byte[] Build() {
    using var framesMs = new MemoryStream();
    foreach (var (id, body) in this._frames) {
      framesMs.Write(Encoding.ASCII.GetBytes(id));
      WriteSyncSafe(framesMs, body.Length);
      framesMs.WriteByte(0); framesMs.WriteByte(0);  // flags
      framesMs.Write(body);
    }
    var framesBytes = framesMs.ToArray();

    using var tag = new MemoryStream();
    tag.Write("ID3"u8);
    tag.WriteByte(0x04); tag.WriteByte(0x00);  // version 2.4
    tag.WriteByte(0x00);                        // flags
    WriteSyncSafe(tag, framesBytes.Length);
    tag.Write(framesBytes);
    return tag.ToArray();
  }

  private static void WriteSyncSafe(Stream ms, int value) {
    ms.WriteByte((byte)((value >> 21) & 0x7F));
    ms.WriteByte((byte)((value >> 14) & 0x7F));
    ms.WriteByte((byte)((value >> 7) & 0x7F));
    ms.WriteByte((byte)(value & 0x7F));
  }

  private static string DetectMime(byte[] picture) {
    if (picture.Length >= 3 && picture[0] == 0xFF && picture[1] == 0xD8 && picture[2] == 0xFF)
      return "image/jpeg";
    if (picture.Length >= 4 && picture[0] == 0x89 && picture[1] == 0x50 &&
        picture[2] == 0x4E && picture[3] == 0x47)
      return "image/png";
    if (picture.Length >= 4 && picture[0] == 0x47 && picture[1] == 0x49 &&
        picture[2] == 0x46 && picture[3] == 0x38)
      return "image/gif";
    return "application/octet-stream";
  }
}
