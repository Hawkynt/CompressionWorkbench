using System.Buffers.Binary;
using FileSystem.CbmNibble;

namespace Compression.Tests.CbmNibble;

[TestFixture]
public class CbmNibbleTests {

  // Build a minimal G64 with `trackCount` tracks of `trackDataSize` GCR bytes each.
  private static byte[] BuildG64(int trackCount = 3, int trackDataSize = 128) {
    const int headerSize = 12;
    var offsetTableSize = trackCount * 4;
    var speedTableSize = trackCount * 4;
    var trackBlockSize = 2 + trackDataSize;              // u16 length + payload
    var totalTrackBlocks = trackCount * trackBlockSize;
    var total = headerSize + offsetTableSize + speedTableSize + totalTrackBlocks;
    var buf = new byte[total];

    // Header: "GCR-1541\0" + version + track_count + max_track_size (u16 LE).
    CbmNibbleReader.G64Signature.CopyTo(buf, 0);
    buf[8] = 0;                                                              // version 0
    buf[9] = (byte)trackCount;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), (ushort)trackDataSize);

    // Offset table + speed table + track blocks.
    var offsetTableStart = headerSize;
    var speedTableStart = offsetTableStart + offsetTableSize;
    var firstTrackOffset = speedTableStart + speedTableSize;

    for (var i = 0; i < trackCount; i++) {
      var blockOffset = firstTrackOffset + i * trackBlockSize;
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offsetTableStart + i * 4), (uint)blockOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(speedTableStart + i * 4), 3u); // zone 3 (innermost)
      // Length-prefixed track block.
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(blockOffset), (ushort)trackDataSize);
      // Fill payload with track-index-derived pattern for round-trip verification.
      for (var j = 0; j < trackDataSize; j++)
        buf[blockOffset + 2 + j] = (byte)((i * 0x11) ^ j);
    }
    return buf;
  }

  [Test, Category("HappyPath")]
  public void Read_G64_ParsesHeaderAndTracks() {
    var data = BuildG64(trackCount: 4, trackDataSize: 64);
    var img = CbmNibbleReader.Read(data, "test.g64");

    Assert.That(img.Kind, Is.EqualTo(CbmNibbleReader.ImageKind.G64));
    Assert.That(img.TrackCount, Is.EqualTo(4));
    Assert.That(img.MaxTrackSize, Is.EqualTo(64));
    Assert.That(img.Tracks, Has.Count.EqualTo(4));
    Assert.That(img.Tracks[2].Data, Has.Length.EqualTo(64));
    Assert.That(img.Tracks[0].SpeedZone, Is.EqualTo(3u));
    // Verify round-tripped pattern (track 1, byte 5 → 0x11 ^ 5).
    Assert.That(img.Tracks[1].Data[5], Is.EqualTo((byte)(0x11 ^ 5)));
  }

  [Test, Category("HappyPath")]
  public void Read_G64_HandlesEmptyTrackOffset() {
    // Build a 3-track image then overwrite track 1's offset with 0 (empty).
    var data = BuildG64(trackCount: 3, trackDataSize: 32);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12 + 1 * 4), 0u);
    var img = CbmNibbleReader.Read(data, "test.g64");

    Assert.That(img.Tracks[0].Data, Is.Not.Empty);
    Assert.That(img.Tracks[1].Data, Is.Empty);
    Assert.That(img.Tracks[2].Data, Is.Not.Empty);
  }

  [Test, Category("HappyPath")]
  public void Read_Nib_SplitsIntoHalfTracks() {
    // Short NIB dump: 4 half-tracks of 8192 bytes each. Extension-detected.
    var data = new byte[4 * CbmNibbleReader.NibTrackSize];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
    var img = CbmNibbleReader.Read(data, "dump.nib");

    Assert.That(img.Kind, Is.EqualTo(CbmNibbleReader.ImageKind.Nib));
    Assert.That(img.TrackCount, Is.EqualTo(4));
    Assert.That(img.Tracks, Has.Count.EqualTo(4));
    Assert.That(img.Tracks[0].Data, Has.Length.EqualTo(CbmNibbleReader.NibTrackSize));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_G64_List_EmitsTrackEntries() {
    var data = BuildG64(trackCount: 3, trackDataSize: 32);
    using var ms = new MemoryStream(data);
    var entries = new G64FormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("track_00.bin"));
    Assert.That(names, Does.Contain("track_01.bin"));
    Assert.That(names, Does.Contain("track_02.bin"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_G64_Extract_WritesMetadataIni() {
    var data = BuildG64(trackCount: 2, trackDataSize: 16);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new G64FormatDescriptor().Extract(ms, tmp, null, null);
      var meta = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(meta), Is.True);
      var text = File.ReadAllText(meta);
      Assert.That(text, Does.Contain("kind = G64"));
      Assert.That(text, Does.Contain("track_count = 2"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Read_UnknownData_Throws() {
    var data = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
    Assert.That(() => CbmNibbleReader.Read(data, "test.bin"),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_G64_ZeroTrackCount_Throws() {
    var data = BuildG64(trackCount: 1);
    data[9] = 0; // zero out track_count in header
    Assert.That(() => CbmNibbleReader.Read(data, "test.g64"),
      Throws.InstanceOf<InvalidDataException>());
  }
}
