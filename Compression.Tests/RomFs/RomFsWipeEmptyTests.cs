#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.RomFs;

namespace Compression.Tests.RomFs;

/// <summary>
/// Unused-space wiping for the ROMFS descriptor. ROMFS is a packed, read-only
/// image: every file's data is stored byte-exact (no cluster rounding), so
/// there is no cluster tip — cluster-tip wiping is not applicable. The only
/// genuinely unused bytes are the 16-byte alignment padding after each file's
/// data. The extent map reports every record header, name and file's data as
/// live, so the wiper zeroes only that padding while leaving live data and
/// metadata intact.
/// </summary>
[TestFixture]
public class RomFsWipeEmptyTests {

  private static byte[] BuildImage(params (string Path, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using var w = new RomFsWriter(ms, leaveOpen: true);
    foreach (var (path, data) in files)
      w.AddFile(path, data);
    w.Finish();
    return ms.ToArray();
  }

  [Test]
  public void RomFsDescriptorImplementsIWipeEmpty() {
    var desc = new RomFsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_AlignmentPadding_ZeroedAndFileRoundTrips() {
    // 20 bytes of data → padded to 32, leaving a 12-byte alignment-padding gap.
    var content = new byte[20];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImage(("secret.bin", content));

    var desc = new RomFsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // The file's data extent is byte-exact; the padding after it is a free gap.
    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.Length == content.Length);
    var padStart = extent.Offset + content.Length;
    var padLen = 12;
    Assert.That(padStart + padLen, Is.LessThanOrEqualTo(ms.Length),
      "padding region must lie within the image");

    // Dirty the alignment padding to prove the wiper cleans it.
    ms.Position = padStart;
    var junk = new byte[padLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk, 0, junk.Length);

    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = padStart;
    var pad = new byte[padLen];
    ms.ReadExactly(pad);
    Assert.That(pad, Is.All.EqualTo((byte)0), "alignment padding must be zeroed (tips are N/A for ROMFS)");

    ms.Position = 0;
    var reader = new RomFsReader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "secret.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "file content survives the wipe intact");
  }
}
