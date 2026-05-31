#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Jffs2;

namespace Compression.Tests.Jffs2;

/// <summary>
/// Unused-space wiping for the JFFS2 descriptor. JFFS2 is a log-structured
/// flash filesystem: file data lives in variably-sized inode nodes packed back
/// to back, with no fixed cluster/block allocation and therefore no cluster
/// tips. Free space is the erased-flash tail (0xFF). Tip wiping is N/A; the
/// wiper zeros only the free regions (gaps and the padded erase-block tail).
/// </summary>
[TestFixture]
public class Jffs2WipeEmptyTests {

  private static byte[] BuildImageWith(string name, byte[] data) {
    var w = new Jffs2Writer();
    w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void Jffs2DescriptorImplementsIWipeEmpty() {
    var desc = new Jffs2FormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_FreeRegionZeroed_AndFileRoundTrips() {
    var content = new byte[200];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith("secret.bin", content);

    var desc = new Jffs2FormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // The free tail is 0xFF-erased flash. Dirty a chunk of it with non-FF junk
    // so we can prove the wipe zeroed it (the erased 0xFF would otherwise be
    // skipped by the wiper's already-clean detection only for 0x00 — 0xFF is
    // non-zero so it gets zeroed regardless).
    ms.Position = 0;
    var free = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Free && e.Length >= 64);
    var dirtyOff = free.Offset;
    var dirtyLen = (int)Math.Min(free.Length, 64);
    ms.Position = dirtyOff;
    var junk = new byte[dirtyLen];
    Array.Fill(junk, (byte)0x5A);
    ms.Write(junk, 0, junk.Length);

    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = dirtyOff;
    var region = new byte[dirtyLen];
    ms.Read(region, 0, region.Length);
    Assert.That(region, Is.All.EqualTo((byte)0), "free flash region must be zeroed");

    // File still reads back: nodes are untouched, tip wiping is N/A.
    ms.Position = 0;
    var reader = new Jffs2FileReader(ms.ToArray());
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "secret.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "file content survives the wipe intact");
  }
}
