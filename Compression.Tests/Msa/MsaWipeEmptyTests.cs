#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Fat;
using FileSystem.Msa;

namespace Compression.Tests.Msa;

/// <summary>
/// Cluster-tip / unused-space wiping for the MSA descriptor. MSA is an outer
/// RLE-compressed container around an inner FAT12 floppy image. The container's
/// compressed bytes have no in-place free space to wipe, so wiping is performed
/// the only honest way: decode the tracks to the flat FAT image, wipe its free
/// clusters and cluster tips, then re-encode preserving geometry. The inner FAT
/// directory records each file's true logical size, so cluster tips are wiped.
/// </summary>
[TestFixture]
public class MsaWipeEmptyTests {

  /// <summary>Builds a flat FAT12 720KB image with one file, then dirties the
  /// file's cluster tip slack with 0xFF so we can prove the wipe zeroes it. The
  /// FAT extent length is clamped to the logical size, so the tip is the gap
  /// between the file end and the end of its (>= 512-byte) allocation cluster.</summary>
  private static (byte[] Flat, long TipStart, int TipLen) BuildDirtyFlat(string name, byte[] content) {
    var fw = new FatWriter();
    fw.AddFile(name, content);
    var disk = fw.Build(totalSectors: 1440);

    using var fs = new MemoryStream(disk, writable: true);
    var extent = new FatFormatDescriptor().EnumerateExtents(fs)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == name);
    var tipStart = extent.Offset + content.Length;
    // Dirty the tip slack inside the last cluster. A FAT12 720KB image uses a
    // >= 512-byte cluster, so 200 bytes of content leaves at least 312 bytes of
    // tip; dirty a conservative 300 bytes that stay within the cluster.
    var tipLen = 300;
    var junk = new byte[tipLen];
    Array.Fill(junk, (byte)0xFF);
    Array.Copy(junk, 0, disk, tipStart, tipLen);
    return (disk, tipStart, tipLen);
  }

  private static MemoryStream WrapInMsa(byte[] flat) {
    var msa = new MemoryStream();
    MsaWriter.Write(msa, flat, sectorsPerTrack: 9, sides: 1);
    msa.Position = 0;
    return msa;
  }

  [Test]
  public void MsaDescriptorImplementsIWipeEmpty() {
    var desc = new MsaFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_InnerClusterTip_ZeroedAndFileRoundTrips() {
    var content = new byte[200]; // < 512-byte cluster — leaves a tip.
    Array.Fill(content, (byte)0xAA);
    var (flat, tipStart, tipLen) = BuildDirtyFlat("SECRET.BIN", content);

    using var msa = WrapInMsa(flat);

    var desc = new MsaFormatDescriptor();
    desc.WipeUnusedSpace(msa, wipeClusterTips: true, wipeDeletedEntries: true);

    // Decode the wiped MSA and inspect the inner FAT image.
    msa.Position = 0;
    var reader = new MsaReader(msa);
    var wipedFlat = reader.Extract(reader.Entries[0]);

    var tip = wipedFlat.AsSpan((int)tipStart, tipLen).ToArray();
    Assert.That(tip, Is.All.EqualTo((byte)0), "inner FAT cluster tip must be zeroed");

    using var fs = new MemoryStream(wipedFlat, writable: false);
    var fr = new FatReader(fs);
    var entry = fr.Entries.First(e => !e.IsDirectory && e.Name == "SECRET.BIN");
    Assert.That(fr.Extract(entry), Is.EqualTo(content), "inner file content survives the wipe intact");
  }
}
