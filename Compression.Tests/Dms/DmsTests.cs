namespace Compression.Tests.Dms;

[TestFixture]
public class DmsTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_StoredTrack() {
    // Create a single track of data (standard Amiga DD track = 11264 bytes for a cylinder)
    var trackData = new byte[11264];
    Random.Shared.NextBytes(trackData);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dms.DmsWriter(ms, leaveOpen: true))
      w.WriteTrack(0, trackData, compressionMode: 0);
    ms.Position = 0;

    var r = new FileFormat.Dms.DmsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].TrackNumber, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(trackData));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RleTrack() {
    // Data with repeated bytes (good for RLE)
    var trackData = new byte[5632];
    for (var i = 0; i < trackData.Length; i++)
      trackData[i] = (byte)(i % 3 == 0 ? 0xAA : i & 0xFF);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dms.DmsWriter(ms, leaveOpen: true))
      w.WriteTrack(0, trackData, compressionMode: 1);
    ms.Position = 0;

    var r = new FileFormat.Dms.DmsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(trackData));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleTracks() {
    var tracks = new byte[3][];
    for (var t = 0; t < 3; t++) {
      tracks[t] = new byte[5632];
      Random.Shared.NextBytes(tracks[t]);
    }

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dms.DmsWriter(ms, leaveOpen: true))
      for (var t = 0; t < 3; t++)
        w.WriteTrack(t, tracks[t], compressionMode: 0);
    ms.Position = 0;

    var r = new FileFormat.Dms.DmsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    for (var t = 0; t < 3; t++) {
      Assert.That(r.Entries[t].TrackNumber, Is.EqualTo(t));
      Assert.That(r.Extract(r.Entries[t]), Is.EqualTo(tracks[t]));
    }
  }

  [Test, Category("HappyPath")]
  public void Magic_IsDMS() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dms.DmsWriter(ms, leaveOpen: true))
      w.WriteTrack(0, new byte[512], compressionMode: 0);
    ms.Position = 0;

    Assert.That(ms.ReadByte(), Is.EqualTo(0x44)); // 'D'
    Assert.That(ms.ReadByte(), Is.EqualTo(0x4D)); // 'M'
    Assert.That(ms.ReadByte(), Is.EqualTo(0x53)); // 'S'
    Assert.That(ms.ReadByte(), Is.EqualTo(0x21)); // '!'
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void WriteDisk_ExtractDisk_RoundTrip() {
    // Small disk image (3 tracks)
    var diskImage = new byte[5632 * 3];
    Random.Shared.NextBytes(diskImage);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dms.DmsWriter(ms, leaveOpen: true))
      w.WriteDisk(diskImage, compressionMode: 0);
    ms.Position = 0;

    var r = new FileFormat.Dms.DmsReader(ms);
    var extracted = r.ExtractDisk();
    Assert.That(extracted, Is.EqualTo(diskImage));
  }
}
