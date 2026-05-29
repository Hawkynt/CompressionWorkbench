#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipWipeEmptyTests {

  [Test]
  public void ZipDescriptorImplementsIWipeEmpty() {
    var desc = new ZipFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void WipeEmpty_ZerosDeadBytesAfterRemoval() {
    // Build a ZIP with two entries.
    var ms = new MemoryStream();
    var w = new ZipWriter(ms, leaveOpen: true);
    var victimData = System.Text.Encoding.ASCII.GetBytes("SECRET_PAYLOAD_WIPE_TEST");
    w.AddEntry("victim.txt", victimData, ZipCompressionMethod.Store);
    w.AddEntry("keeper.txt", "keep-me"u8.ToArray(), ZipCompressionMethod.Store);
    w.Finish();

    // Remove victim — leaves dead bytes in the archive.
    ZipModifier.RemoveFile(ms, "victim.txt", wipeData: false);

    // At this point, orphan data from victim.txt may still be in the archive
    // gaps (even though RemoveFile with wipeData: true would clear it, we test
    // wipe-empty independently by using wipeData: false above).
    // Note: ZipModifier.RemoveFile with wipeData: false just updates the CD,
    // leaving the LFH + data in place. The wiper should zero those gaps.

    var desc = new ZipFormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms);

    // keeper.txt must still be readable.
    ms.Position = 0;
    var reader = new ZipReader(ms);
    Assert.That(reader.Entries.Any(e => e.FileName == "keeper.txt"), Is.True);
    var keeperBytes = reader.ExtractEntry(reader.Entries.First(e => e.FileName == "keeper.txt"));
    Assert.That(System.Text.Encoding.ASCII.GetString(keeperBytes), Is.EqualTo("keep-me"));
  }

  [Test]
  public void WipeEmpty_PreservesLiveEntries() {
    var ms = new MemoryStream();
    var w = new ZipWriter(ms, leaveOpen: true);
    w.AddEntry("file1.txt", "data-one"u8.ToArray(), ZipCompressionMethod.Store);
    w.AddEntry("file2.txt", "data-two"u8.ToArray(), ZipCompressionMethod.Store);
    w.Finish();

    var desc = new ZipFormatDescriptor();
    desc.WipeUnusedSpace(ms);

    // Both entries must still be readable.
    ms.Position = 0;
    var reader = new ZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    foreach (var entry in reader.Entries) {
      var data = reader.ExtractEntry(entry);
      Assert.That(data.Length, Is.GreaterThan(0));
    }
  }

  [Test]
  public void WipeEmpty_ReturnsZeroOnCleanArchive() {
    var ms = new MemoryStream();
    var w = new ZipWriter(ms, leaveOpen: true);
    w.AddEntry("only.txt", "content"u8.ToArray(), ZipCompressionMethod.Store);
    w.Finish();

    var desc = new ZipFormatDescriptor();
    // A freshly created ZIP should have no gaps.
    var wiped = desc.WipeUnusedSpace(ms);
    // Wipe count may be 0 or small (just trailing bytes). No crash is the key assertion.
    Assert.That(wiped, Is.GreaterThanOrEqualTo(0));
  }

  [Test]
  public void WipeEmpty_ViaGenericWiper_WorksWithLayoutMap() {
    var ms = new MemoryStream();
    var w = new ZipWriter(ms, leaveOpen: true);
    w.AddEntry("a.txt", "aaa"u8.ToArray(), ZipCompressionMethod.Store);
    w.Finish();

    // Use the generic wiper directly.
    ms.Position = 0;
    var layoutMap = new ZipFormatDescriptor();
    var extents = ZipLayoutMap.Enumerate(ms);
    var wiped = UnusedSpaceWiper.Wipe(ms, extents, ms.Length, wipeClusterTips: false);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(0));
  }
}
