#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Udf;

namespace Compression.Tests.Udf;

[TestFixture]
public class UdfWipeEmptyTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new UdfWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test]
  public void Descriptor_ImplementsIWipeEmpty() {
    Assert.That(new UdfFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  // UDF stores file data in 2048-byte sectors. A file shorter than one sector
  // leaves a tip — the padding between the file's logical end and the sector
  // boundary. The allocation descriptor records the logical byte length, so the
  // file's Used extent ends exactly at its real size; the tip is uncovered space
  // the wiper must zero. The live file must still round-trip afterwards.
  [Test]
  public void WipeEmpty_ZerosClusterTip_AndFileRoundTrips() {
    var content = new byte[1000]; // under one 2048-byte sector
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("data.bin", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Locate the file's data extent (Used, FileName == entry name).
    ms.Position = 0;
    var fileExtent = UdfExtentMap.Enumerate(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "data.bin"
                  && e.Classification != DefragBlockClass.Directory);
    Assert.That(fileExtent.Length, Is.GreaterThan(content.Length),
      "The data extent is sector-padded, so a tip exists beyond the logical size");

    // Dirty the cluster tip (immediately after the file's logical end).
    var tipOffset = fileExtent.Offset + content.Length;
    ms.Position = tipOffset;
    var junk = new byte[48];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk);

    ms.Position = tipOffset;
    Assert.That(ms.ReadByte(), Is.EqualTo(0xFF), "Precondition: dirtied cluster tip");

    var desc = new UdfFormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0), "Should have wiped the dirtied tip");

    // The tip must now be all zero.
    ms.Position = tipOffset;
    var tipBytes = new byte[48];
    ms.ReadExactly(tipBytes);
    Assert.That(tipBytes, Is.All.EqualTo((byte)0), "Cluster tip must be zeroed");

    // File still round-trips intact.
    ms.Position = 0;
    var reader = new UdfReader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "data.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "File content must survive the wipe");
  }
}
