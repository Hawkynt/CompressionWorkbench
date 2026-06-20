using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.DoubleSpace;
using FileSystem.DriveSpace3;
using FileSystem.Stacker;

namespace Compression.Tests.Cvf;

/// <summary>
/// Volume-label + timestamp metadata knobs on the genuine CVF writers must
/// survive a round trip through the matching genuine reader, and the timestamp
/// must be encoded into the FAT directory entry per spec — without disturbing
/// file extraction.
/// </summary>
[TestFixture]
public class GenuineMetadataTests {

  private static readonly byte[] Payload =
    Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("metadata test\r\n", 30)));
  private static readonly DateTime Stamp = new(1996, 8, 24, 13, 30, 20, DateTimeKind.Utc);

  [Test]
  public void FatDirStamp_Encode_MatchesFatBitLayout() {
    var (time, date) = FatDirStamp.Encode(Stamp);
    Assert.That(date, Is.EqualTo((ushort)(((1996 - 1980) << 9) | (8 << 5) | 24)));
    Assert.That(time, Is.EqualTo((ushort)((13 << 11) | (30 << 5) | (20 / 2))));
    Assert.That(FatDirStamp.Encode(new DateTime(1970, 1, 1)), Is.EqualTo(((ushort)0, (ushort)0)),
      "pre-1980 timestamps are unset");
  }

  [Test]
  public void DoubleSpaceV2_Label_RoundTrips() {
    var w = new GenuineCvfWriter { VolumeLabel = "DISK1", Timestamp = Stamp };
    w.AddFile("HELLO.TXT", Payload);
    using var r = new GenuineCvfReader(new MemoryStream(w.Build()));
    Assert.That(r.VolumeLabel, Is.EqualTo("DISK1"));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "HELLO.TXT")), Is.EqualTo(Payload));
  }

  [Test]
  public void DriveSpace3_Label_And_Timestamp_RoundTrip() {
    var w = new GenuineDvr3Writer { VolumeLabel = "WIN95", Timestamp = Stamp };
    w.AddFile("HELLO.TXT", Payload);
    var img = w.Build();

    using var r = new GenuineDvr3Reader(new MemoryStream(img));
    Assert.That(r.VolumeLabel, Is.EqualTo("WIN95"));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "HELLO.TXT")), Is.EqualTo(Payload));

    // The file's FAT directory entry must carry the encoded date/time.
    var de = FindDirEntry(img, "HELLO   TXT");
    var (time, date) = FatDirStamp.Encode(Stamp);
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(de + 22)), Is.EqualTo(time));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(de + 24)), Is.EqualTo(date));
  }

  [Test]
  public void Stacker_Label_RoundTrips() {
    var w = new GenuineStackerWriter { VolumeLabel = "STAC", Timestamp = Stamp };
    w.AddFile("HELLO.TXT", Payload);
    using var r = new GenuineStackerReader(new MemoryStream(w.Build()));
    Assert.That(r.VolumeLabel, Is.EqualTo("STAC"));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "HELLO.TXT")), Is.EqualTo(Payload));
  }

  // Locates a 32-byte FAT directory entry by its 11-byte 8.3 short name.
  private static int FindDirEntry(byte[] img, string shortName83) {
    var needle = Encoding.ASCII.GetBytes(shortName83);
    for (var i = 0; i + 32 <= img.Length; i += 32) {
      if (img.AsSpan(i, 11).SequenceEqual(needle)) return i;
    }
    Assert.Fail($"dir entry '{shortName83}' not found");
    return -1;
  }
}
