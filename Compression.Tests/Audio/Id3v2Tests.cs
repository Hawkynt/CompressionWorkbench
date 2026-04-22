#pragma warning disable CS1591
using System.Text;
using FileFormat.Mp3;

namespace Compression.Tests.Audio;

[TestFixture]
public class Id3v2Tests {

  // Builds a minimal ID3v2.4 tag with a TIT2 title frame + an APIC cover image.
  private static byte[] MakeTag(string title, byte[] picture, string mime) {
    // TIT2 frame body: 1 encoding byte (0x03 = UTF-8) + title UTF-8 bytes
    var titleBytes = Encoding.UTF8.GetBytes(title);
    var tit2Body = new byte[1 + titleBytes.Length];
    tit2Body[0] = 0x03;
    titleBytes.CopyTo(tit2Body, 1);

    // APIC body: encoding (0x03) + mime + 0x00 + picture type (0x03 = cover) + description + 0x00 + picture bytes
    var mimeBytes = Encoding.ASCII.GetBytes(mime);
    using var apicMs = new MemoryStream();
    apicMs.WriteByte(0x03);
    apicMs.Write(mimeBytes);
    apicMs.WriteByte(0x00);
    apicMs.WriteByte(0x03);
    apicMs.Write(Encoding.UTF8.GetBytes("Cover"));
    apicMs.WriteByte(0x00);
    apicMs.Write(picture);
    var apicBody = apicMs.ToArray();

    // Assemble frames (10-byte header each)
    using var framesMs = new MemoryStream();
    AppendFrame(framesMs, "TIT2", tit2Body);
    AppendFrame(framesMs, "APIC", apicBody);
    var frames = framesMs.ToArray();

    // Tag header: "ID3" + 0x04 0x00 (v2.4) + flags (0x00) + sync-safe size
    using var tag = new MemoryStream();
    tag.Write("ID3"u8);
    tag.WriteByte(0x04); tag.WriteByte(0x00); tag.WriteByte(0x00);
    WriteSyncSafe(tag, frames.Length);
    tag.Write(frames);
    return tag.ToArray();
  }

  private static void AppendFrame(Stream ms, string id, byte[] body) {
    ms.Write(Encoding.ASCII.GetBytes(id));
    WriteSyncSafe(ms, body.Length);
    ms.WriteByte(0); ms.WriteByte(0); // flags
    ms.Write(body);
  }

  private static void WriteSyncSafe(Stream ms, int value) {
    ms.WriteByte((byte)((value >> 21) & 0x7F));
    ms.WriteByte((byte)((value >> 14) & 0x7F));
    ms.WriteByte((byte)((value >> 7) & 0x7F));
    ms.WriteByte((byte)(value & 0x7F));
  }

  [Test]
  public void ReadsTitleFrame() {
    var tag = MakeTag("Hello World", [0xFF, 0xD8, 0xFF], "image/jpeg");
    var (_, frames) = new Id3v2Reader().Read(tag);
    Assert.That(frames, Is.Not.Empty);
    var title = frames.First(f => f.Id == "TIT2");
    Assert.That(Encoding.UTF8.GetString(title.Payload), Is.EqualTo("Hello World"));
  }

  [Test]
  public void ReadsApicPicturePayload() {
    var pic = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
    var tag = MakeTag("X", pic, "image/jpeg");
    var (_, frames) = new Id3v2Reader().Read(tag);
    var apic = frames.First(f => f.Id == "APIC");
    Assert.That(apic.MimeType, Is.EqualTo("image/jpeg"));
    Assert.That(apic.Description, Is.EqualTo("Cover"));
    Assert.That(apic.Payload, Is.EqualTo(pic));
  }

  [Test]
  public void Mp3Descriptor_ExposesCoverAsEntry() {
    // Tag + a single byte of "audio" payload so the full MP3 is non-empty.
    var tag = MakeTag("Song", [0x89, 0x50, 0x4E, 0x47], "image/png");
    var file = new byte[tag.Length + 1];
    tag.CopyTo(file, 0);
    file[^1] = 0x00;

    using var ms = new MemoryStream(file);
    var entries = new Mp3FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "cover.png"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.mp3"));
  }
}
