#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.CramFs;

namespace Compression.Tests.CramFs;

[TestFixture]
public class CramFsWipeEmptyTests {

  private static byte[] BuildImageWith(params (string Path, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new CramFsWriter(ms, leaveOpen: true))
      foreach (var (path, data) in files) w.AddFile(path, data);
    return ms.ToArray();
  }

  [Test]
  public void Descriptor_ImplementsIWipeEmpty() {
    Assert.That(new CramFsFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  // CramFS is a compressed, read-only ROM image: superblock + inode tables +
  // compressed block data are packed back-to-back with no free regions and no
  // cluster tips (data is packed at the compressed-block level). Wiping is a
  // no-op; cluster tips are N/A. The file must still round-trip afterwards.
  [Test]
  public void WipeEmpty_IsNoOp_AndFileRoundTrips() {
    var content = new byte[100];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("/tiny.bin", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var before = ms.ToArray();
    var desc = new CramFsFormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    Assert.That(wiped, Is.EqualTo(0), "Fully packed read-only image has nothing to wipe");
    Assert.That(ms.ToArray(), Is.EqualTo(before), "Image bytes must be unchanged");

    // File still round-trips.
    ms.Position = 0;
    var r = new CramFsReader(ms);
    var entry = r.Entries.First(e => e.IsRegularFile && e.Name == "tiny.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(content));
  }
}
