#pragma warning disable CS1591
using System.Text;
using FileFormat.Mp3;

namespace Compression.Tests.Audio;

[TestFixture]
public class Id3v1Tests {

  // Builds a 128-byte ID3v1 trailer.
  private static byte[] MakeTag(string title, string artist, string album, string year,
                                 string comment, int? track = null, byte genre = 0) {
    var tag = new byte[128];
    "TAG"u8.CopyTo(tag);
    Encoding.Latin1.GetBytes(title).AsSpan(0, Math.Min(30, title.Length)).CopyTo(tag.AsSpan(3));
    Encoding.Latin1.GetBytes(artist).AsSpan(0, Math.Min(30, artist.Length)).CopyTo(tag.AsSpan(33));
    Encoding.Latin1.GetBytes(album).AsSpan(0, Math.Min(30, album.Length)).CopyTo(tag.AsSpan(63));
    Encoding.Latin1.GetBytes(year).AsSpan(0, Math.Min(4, year.Length)).CopyTo(tag.AsSpan(93));
    if (track.HasValue) {
      // ID3v1.1: 28-byte comment + 0x00 + track byte
      Encoding.Latin1.GetBytes(comment).AsSpan(0, Math.Min(28, comment.Length)).CopyTo(tag.AsSpan(97));
      tag[125] = 0;
      tag[126] = (byte)track.Value;
    } else {
      Encoding.Latin1.GetBytes(comment).AsSpan(0, Math.Min(30, comment.Length)).CopyTo(tag.AsSpan(97));
    }
    tag[127] = genre;
    return tag;
  }

  [Test]
  public void ParsesV1Tag() {
    var file = new byte[200];
    MakeTag("My Song", "Artist", "Album", "2024", "Great").CopyTo(file.AsSpan(72));
    var result = new Id3v1Reader().Read(file);
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Title, Is.EqualTo("My Song"));
    Assert.That(result.Artist, Is.EqualTo("Artist"));
    Assert.That(result.Year, Is.EqualTo("2024"));
    Assert.That(result.Track, Is.Null);
  }

  [Test]
  public void DetectsV1_1TrackField() {
    var file = new byte[200];
    MakeTag("T", "A", "L", "2024", "C", track: 7).CopyTo(file.AsSpan(72));
    var result = new Id3v1Reader().Read(file);
    Assert.That(result!.Track, Is.EqualTo(7));
  }

  [Test]
  public void ReturnsNullWhenNoTag() {
    Assert.That(new Id3v1Reader().Read(new byte[200]), Is.Null);
  }

  [Test]
  public void Mp3Descriptor_V1OnlyFileEmitsMetadataIniAtRoot() {
    // File: 100 bytes of "audio" + 128-byte v1 trailer.
    var file = new byte[228];
    for (var i = 0; i < 100; ++i) file[i] = 0xAA;
    MakeTag("T1", "A1", "L1", "2024", "C1").CopyTo(file.AsSpan(100));

    using var ms = new MemoryStream(file);
    var entries = new Mp3FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "id3v1/metadata.ini"), Is.False,
      "v1-only file should have metadata.ini at root, no id3v1/ sub-folder");
  }
}
