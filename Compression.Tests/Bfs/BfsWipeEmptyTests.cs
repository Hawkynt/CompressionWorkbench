#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Bfs;

namespace Compression.Tests.Bfs;

[TestFixture]
public class BfsWipeEmptyTests {

  private const int BlockSize = 1024;

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new BfsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void DescriptorOffersWipeEmptyCapability() {
    Assert.That(new BfsFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void WipingClusterTip_ZerosBlockSlack_AndKeepsFileIntact() {
    // A file shorter than one 1024-byte block leaves tail slack in its block.
    const int fileSize = 700;
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("data.bin", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var desc = new BfsFormatDescriptor();

    // Locate the file's used data extent.
    ms.Position = 0;
    var fileExtent = desc.EnumerateExtents(ms)
      .Single(e => e.Kind == DefragBlockKind.Used && e.FileName == "data.bin");

    // The allocated block runs to the next 1024 boundary; dirty the slack.
    var blockEnd = ((fileExtent.Offset + fileSize + BlockSize - 1) / BlockSize) * BlockSize;
    var slackLen = (int)(blockEnd - (fileExtent.Offset + fileSize));
    Assert.That(slackLen, Is.GreaterThan(0), "Block slack expected");

    ms.Position = fileExtent.Offset + fileSize;
    var junk = new byte[slackLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk);

    // When unused space is wiped with cluster tips enabled ...
    var wiped = desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0));

    // ... the live file still round-trips through the reader unchanged ...
    ms.Position = 0;
    var reader = new BfsReader(ms);
    var entry = reader.Entries.Single(e => !e.IsDirectory && e.Name == "data.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(content));

    // ... and the block tip (file end .. block end) is all zero.
    var tip = new byte[slackLen];
    ms.Position = fileExtent.Offset + fileSize;
    _ = ms.Read(tip, 0, tip.Length);
    Assert.That(tip, Is.All.Zero, "Block cluster tip must be wiped");
  }
}
