using Compression.Registry;
using FileSystem.CbmNibble;

namespace Compression.Tests.CbmNibble;

[TestFixture]
public sealed class CbmNibbleFilesystemDriverTests {
  [TestCase(false)]
  [TestCase(true)]
  [Category("Driver")]
  public void CanonicalNibbleImage_SupportsFilesystemRoundTrip(bool nib) {
    var original = Enumerable.Range(0, 900).Select(i => (byte)(i * 29)).ToArray();
    var writer = new CbmNibbleWriter();
    writer.AddFile("HELLO", original);
    var bytes = nib ? writer.BuildNib() : writer.Build();

    using var image = new MemoryStream();
    image.Write(bytes);
    image.Position = 0;

    IFilesystemDriverProvider filesystem = nib ? new NibFormatDescriptor() : new G64FormatDescriptor();
    var beforeTracks = CbmNibbleReader.Read(image.ToArray(), nib ? "image.nib" : "image.g64");
    var untouchedBefore = beforeTracks.Tracks.Single(track => track.Index == 68).Data.ToArray();

    var profile = filesystem.ProbeFilesystem(image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
      Assert.That(profile.CanMountWritable, Is.True, string.Join("; ", profile.Limitations));
      Assert.That(profile.MutationModel, Is.EqualTo(FilesystemMutationModel.Direct));
    });

    using (var fs = filesystem.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var root = fs.RootNodeId;
      var helloId = fs.Lookup(root, "HELLO");
      Assert.That(helloId.HasValue, Is.True);
      using (var hello = fs.OpenFile(helloId!.Value, FileAccess.Read)) {
        var probe = new byte[111];
        Assert.That(hello.Read(257, probe), Is.EqualTo(probe.Length));
        Assert.That(probe, Is.EqualTo(original.AsSpan(257, probe.Length).ToArray()));
      }

      var newId = fs.CreateFile(root, "MOUNTED");
      using (var handle = fs.OpenFile(newId, FileAccess.ReadWrite)) {
        handle.Write(0, "filesystem-driver"u8);
        handle.Write(32, "gcr"u8);
        handle.SetLength(40);
      }
      fs.Rename(root, "MOUNTED", root, "RENAMED", replace: false);
      fs.DeleteFile(root, "HELLO");
      fs.Flush();
    }

    var afterTracks = CbmNibbleReader.Read(image.ToArray(), nib ? "image.nib" : "image.g64");
    Assert.That(afterTracks.Tracks.Single(track => track.Index == 68).Data, Is.EqualTo(untouchedBefore),
      "filesystem writes must not rewrite an unrelated track");

    image.Position = 0;
    using var reopened = filesystem.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.That(reopened.Lookup(reopened.RootNodeId, "HELLO"), Is.Null);
    var renamedId = reopened.Lookup(reopened.RootNodeId, "RENAMED");
    Assert.That(renamedId.HasValue, Is.True);
    using var renamed = reopened.OpenFile(renamedId!.Value, FileAccess.Read);
    var result = new byte[40];
    Assert.That(renamed.Read(0, result), Is.EqualTo(result.Length));
    Assert.Multiple(() => {
      Assert.That(result.AsSpan(0, 17).ToArray(), Is.EqualTo("filesystem-driver"u8.ToArray()));
      Assert.That(result.AsSpan(17, 15).ToArray(), Is.EqualTo(new byte[15]));
      Assert.That(result.AsSpan(32, 3).ToArray(), Is.EqualTo("gcr"u8.ToArray()));
      Assert.That(result.AsSpan(35, 5).ToArray(), Is.EqualTo(new byte[5]));
    });
  }

  [Test, Category("Driver"), Category("EdgeCase")]
  public void G64SectorProjection_DecodesNonByteAlignedTrackRotation() {
    var writer = new CbmNibbleWriter();
    writer.AddFile("HELLO", Enumerable.Range(0, 600).Select(i => (byte)i).ToArray());
    using var image = new MemoryStream();
    image.Write(writer.Build());
    image.Position = 0;

    using (var tracks = CbmNibbleRawTrackDevices.OpenG64(image, writable: true, leaveOpen: true)) {
      var info = tracks.EnumerateTracks().Single(track => track.Index == 0);
      var raw = new byte[(int)info.Length];
      Assert.That(tracks.ReadTrack(0, raw), Is.EqualTo(raw.Length));
      tracks.WriteTrack(0, RotateBits(raw, 3));
      tracks.Flush();
    }

    image.Position = 0;
    var profile = new G64FormatDescriptor().ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
  }

  [Test, Category("Driver"), Category("EdgeCase")]
  public void G64WritableMount_RejectsMeaningfulOddHalfTrack() {
    var writer = new CbmNibbleWriter();
    writer.AddFile("HELLO", [1, 2, 3, 4]);
    using var image = new MemoryStream();
    image.Write(writer.Build());
    image.Position = 0;

    using (var tracks = CbmNibbleRawTrackDevices.OpenG64(image, writable: true, leaveOpen: true)) {
      tracks.WriteTrack(1, Enumerable.Repeat((byte)0xA5, 128).ToArray(), encodingParameter: 3);
      tracks.Flush();
    }

    image.Position = 0;
    var profile = new G64FormatDescriptor().ProbeFilesystem(image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True, "standard whole tracks remain readable");
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(profile.Limitations.Any(x => x.Contains("half-track", StringComparison.OrdinalIgnoreCase)), Is.True);
    });
  }

  private static byte[] RotateBits(byte[] source, int shift) {
    var totalBits = source.Length * 8;
    var result = new byte[source.Length];
    for (var destinationBit = 0; destinationBit < totalBits; ++destinationBit) {
      var sourceBit = (destinationBit + shift) % totalBits;
      var value = (source[sourceBit >> 3] >> (7 - (sourceBit & 7))) & 1;
      result[destinationBit >> 3] |= (byte)(value << (7 - (destinationBit & 7)));
    }
    return result;
  }
}
