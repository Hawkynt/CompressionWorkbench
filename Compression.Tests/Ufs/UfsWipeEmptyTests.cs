#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

[TestFixture]
public class UfsWipeEmptyTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new UfsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test]
  public void Descriptor_ImplementsIWipeEmpty() {
    Assert.That(new UfsFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  // UFS1 allocates file data in blocks/fragments. A file shorter than one block
  // leaves a tip — the padding between the inode's logical di_size and the block
  // boundary. The extent map ends a file's Used run at its logical size, so the
  // tip is uncovered space the wiper must zero. The live file must still
  // round-trip afterwards.
  [Test]
  public void WipeEmpty_ZerosClusterTip_AndFileRoundTrips() {
    var content = new byte[4000]; // under one 8K block
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("data.bin", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Locate the file's data extent (Used, FileName == entry name, not a dir).
    ms.Position = 0;
    var fileExtent = UfsExtentMap.Enumerate(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "data.bin"
                  && e.Classification != DefragBlockClass.Directory);
    Assert.That(fileExtent.Length, Is.EqualTo(content.Length),
      "UFS extent length should equal the logical file size");

    // Dirty the cluster tip (immediately after the file's logical end).
    var tipOffset = fileExtent.Offset + content.Length;
    ms.Position = tipOffset;
    var junk = new byte[64];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk);

    ms.Position = tipOffset;
    Assert.That(ms.ReadByte(), Is.EqualTo(0xFF), "Precondition: dirtied cluster tip");

    var desc = new UfsFormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0), "Should have wiped the dirtied tip");

    // The tip must now be all zero.
    ms.Position = tipOffset;
    var tipBytes = new byte[64];
    ms.ReadExactly(tipBytes);
    Assert.That(tipBytes, Is.All.EqualTo((byte)0), "Cluster tip must be zeroed");

    // File still round-trips intact.
    ms.Position = 0;
    var reader = new UfsReader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "data.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "File content must survive the wipe");
  }
}
