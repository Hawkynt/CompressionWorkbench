#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.SquashFs;

namespace Compression.Tests.SquashFs;

[TestFixture]
public class SquashFsWipeEmptyTests {

  private static byte[] BuildImageWith(params (string Path, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true))
      foreach (var (path, data) in files) w.AddFile(path, data);
    return ms.ToArray();
  }

  [Test]
  public void Descriptor_ImplementsIWipeEmpty() {
    Assert.That(new SquashFsFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  // SquashFS is a compressed, read-only image: superblock + compressed data
  // blocks + metadata tables are packed back-to-back with no free regions and no
  // cluster tips (file data is stored at the compressed-block level). Wiping is a
  // no-op; cluster tips are N/A. The file must still round-trip afterwards.
  [Test]
  public void WipeEmpty_IsNoOp_AndFileRoundTrips() {
    var content = new byte[200];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("/tiny.bin", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var before = ms.ToArray();
    var desc = new SquashFsFormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    Assert.That(wiped, Is.EqualTo(0), "Fully packed read-only image has nothing to wipe");
    Assert.That(ms.ToArray(), Is.EqualTo(before), "Image bytes must be unchanged");

    ms.Position = 0;
    var r = new SquashFsReader(ms);
    var entry = r.Entries.First(e => !e.IsDirectory && e.FullPath.EndsWith("tiny.bin", StringComparison.Ordinal));
    Assert.That(r.Extract(entry), Is.EqualTo(content));
  }
}
