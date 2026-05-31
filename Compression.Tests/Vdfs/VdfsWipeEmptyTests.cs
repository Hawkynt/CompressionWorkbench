using Compression.Registry;

namespace Compression.Tests.Vdfs;

/// <summary>
/// Behaviour: VDFS is a packed archive — file data is stored contiguously and
/// each entry's extent length equals its logical size, so there is no
/// cluster-tip slack to scrub (tips N/A). The meaningful unused space is any
/// region not claimed by the header/entry table or a live file extent, e.g.
/// dead bytes left behind by a shrunk/edited container. This verifies such a
/// trailing free region is zeroed while the live file round-trips.
/// </summary>
[TestFixture]
public class VdfsWipeEmptyTests {

  [Test, Category("HappyPath"), Category("WipeEmpty")]
  public void WipeUnusedSpace_ZeroesTrailingFreeRegion_AndPreservesFile() {
    // Given a one-file image with a dirtied free region appended after the
    // packed payload (simulating dead space beyond the live extents).
    var content = new byte[200];
    Array.Fill(content, (byte)0xAA);

    var w = new FileSystem.Vdfs.VdfsWriter();
    w.AddFile("keep.bin", content);
    var image = w.Build();

    var freeOffset = image.Length;
    const int freeLen = 256;
    var withFree = new byte[image.Length + freeLen];
    image.CopyTo(withFree, 0);
    for (var i = freeOffset; i < withFree.Length; i++) withFree[i] = 0xBB;

    using var ms = new MemoryStream();
    ms.Write(withFree);
    ms.Position = 0;

    var d = new FileSystem.Vdfs.VdfsFormatDescriptor();

    // When unused space is wiped.
    var wiped = ((IWipeEmpty)d).WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    // Then the dead trailing region was scrubbed.
    Assert.That(wiped, Is.GreaterThanOrEqualTo((long)freeLen));

    ms.Position = 0;
    var buf = ms.ToArray();
    for (var i = freeOffset; i < freeOffset + freeLen; i++)
      Assert.That(buf[i], Is.EqualTo(0), $"free byte at {i} must be zeroed");

    // And the live file round-trips intact.
    ms.Position = 0;
    var r = new FileSystem.Vdfs.VdfsReader(ms);
    var entry = r.Entries.First(e => !e.IsDirectory && e.Name == "keep.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(content), "file content must survive the wipe");
  }
}
