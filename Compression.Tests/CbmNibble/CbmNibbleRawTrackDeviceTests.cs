using Compression.Registry;
using FileSystem.CbmNibble;

namespace Compression.Tests.CbmNibble;

[TestFixture]
public sealed class CbmNibbleRawTrackDeviceTests {
  [Test, Category("Driver")]
  public void NibTrackDevice_PerformsPositionalTrackWritesWithoutRebuildingOtherSlots() {
    using var image = new MemoryStream(new byte[CbmNibbleReader.NibExpectedFileSize], writable: true);
    var first = Enumerable.Repeat((byte)0x44, CbmNibbleReader.NibTrackSize).ToArray();
    var second = Enumerable.Repeat((byte)0x99, CbmNibbleReader.NibTrackSize).ToArray();

    using (var device = CbmNibbleRawTrackDevices.OpenNib(image, writable: true)) {
      Assert.That(device.TrackCount, Is.EqualTo(84));
      device.WriteTrack(2, first);
      device.WriteTrack(40, second);
      device.Flush();

      var buffer = new byte[CbmNibbleReader.NibTrackSize];
      Assert.That(device.ReadTrack(2, buffer), Is.EqualTo(buffer.Length));
      Assert.That(buffer, Is.EqualTo(first));
      device.ClearTrack(2);
      Assert.That(device.ReadTrack(2, buffer), Is.EqualTo(0));
      Assert.That(device.ReadTrack(40, buffer), Is.EqualTo(buffer.Length));
      Assert.That(buffer, Is.EqualTo(second));
    }
  }

  [Test, Category("Driver")]
  public void G64TrackDevice_PreservesStableTrackIndexesAcrossCommit() {
    var initial = CbmNibbleWriter.BuildG64FromTracks([
      new CbmNibbleReader.Track(0, new byte[] { 1, 2, 3 }, 3),
      new CbmNibbleReader.Track(4, new byte[] { 7, 8, 9 }, 3),
    ], trackCount: 6);
    using var image = new MemoryStream(initial, writable: true);

    using (var device = CbmNibbleRawTrackDevices.OpenG64(image, writable: true)) {
      var replacement = new byte[] { 9, 9, 9, 9, 9 };
      device.WriteTrack(0, replacement);
      device.WriteTrack(5, new byte[] { 0x55, 0xAA }, encodingParameter: 3);
      device.ClearTrack(4);
      device.Flush();

      var buffer = new byte[64];
      Assert.That(device.ReadTrack(0, buffer), Is.EqualTo(replacement.Length));
      Assert.That(buffer.AsSpan(0, replacement.Length).ToArray(), Is.EqualTo(replacement));
      Assert.That(device.ReadTrack(4, buffer), Is.EqualTo(0));
      Assert.That(device.ReadTrack(5, buffer), Is.EqualTo(2));
    }

    var parsed = CbmNibbleReader.Read(image.ToArray(), "image.g64");
    Assert.That(parsed.TrackCount, Is.EqualTo(6));
    Assert.That(parsed.Tracks.Single(t => t.Index == 0).Data, Is.EqualTo(new byte[] { 9, 9, 9, 9, 9 }));
    Assert.That(parsed.Tracks.Single(t => t.Index == 4).Data, Is.Empty);
    Assert.That(parsed.Tracks.Single(t => t.Index == 5).Data, Is.EqualTo(new byte[] { 0x55, 0xAA }));
  }

  [Test, Category("Driver"), Category("EdgeCase")]
  public void G64TrackDevice_RejectsWritableVariableSpeedProfile() {
    var imageBytes = CbmNibbleWriter.BuildG64FromTracks([
      new CbmNibbleReader.Track(0, new byte[] { 1 }, 3),
    ], trackCount: 1);
    // one track => speed table begins at 12 + 4
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(imageBytes.AsSpan(16, 4), 0x100);
    using var image = new MemoryStream(imageBytes, writable: true);

    Assert.That(() => CbmNibbleRawTrackDevices.OpenG64(image, writable: true),
      Throws.InstanceOf<NotSupportedException>());
    using var readOnly = CbmNibbleRawTrackDevices.OpenG64(image, writable: false);
    Assert.That(readOnly.CanWrite, Is.False);
  }
}
