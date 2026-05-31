using Compression.Registry;
using FileSystem.Ext;

namespace Compression.Tests.Ext;

/// <summary>
/// Unused-space wiping for ext2/3/4 images. The descriptor implements
/// <see cref="IWipeEmpty"/> so free blocks and block-tip slack (the bytes
/// between a file's real size and the end of its last allocated block) can be
/// zeroed without disturbing live file content.
/// </summary>
[TestFixture]
public class ExtWipeEmptyTests {

  // The default ext writer uses 1 KiB blocks; a payload a bit shorter than one
  // block guarantees a non-empty block tip after the last valid byte.
  private const int BlockSize = 1024;

  private static byte[] BuildImageWithOneShortFile(string name, out int payloadLength) {
    payloadLength = 1000; // < 1024 block
    var payload = new byte[payloadLength];
    Array.Fill(payload, (byte)0xAA);

    var w = new ExtWriter();
    w.AddFile(name, payload);
    return w.Build(); // 1 KiB blocks by default
  }

  [Test, Category("Wipe")]
  public void WipeUnusedSpace_LeavesFileContentIntact() {
    var disk = BuildImageWithOneShortFile("payload.bin", out var payloadLength);
    using var ms = new MemoryStream();
    ms.Write(disk);

    new ExtFormatDescriptor().WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = 0;
    var reader = new ExtReader(ms);
    var entry = reader.Entries.Single(e => !e.IsDirectory && e.Name == "payload.bin");
    var roundTripped = reader.Extract(entry);

    Assert.That(roundTripped.Length, Is.EqualTo(payloadLength), "file size preserved after wipe");
    Assert.That(roundTripped, Is.All.EqualTo((byte)0xAA), "file content intact after wipe");
  }

  [Test, Category("Wipe")]
  public void WipeUnusedSpace_ZerosBlockTipOfLastFile() {
    var disk = BuildImageWithOneShortFile("payload.bin", out var payloadLength);
    using var ms = new MemoryStream();
    ms.Write(disk);

    ms.Position = 0;
    var descriptor = new ExtFormatDescriptor();
    var fileExtent = descriptor.EnumerateExtents(ms)
                               .Where(e => e.Kind == DefragBlockKind.Used && e.FileName == "payload.bin")
                               .OrderByDescending(e => e.Offset)
                               .First();

    // The ext block-pointer walker truncates the last run to the valid byte
    // count, so the tip begins at the run's end and spans the rest of the block.
    var tipStart = fileExtent.Offset + fileExtent.Length;
    var blockEnd = fileExtent.Offset - (fileExtent.Offset % BlockSize) + BlockSize;
    var tipLength = blockEnd - tipStart;
    Assert.That(tipLength, Is.GreaterThan(0), "test image must have a non-empty block tip");

    descriptor.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    var bytes = ms.ToArray();
    for (var i = tipStart; i < blockEnd; i++)
      Assert.That(bytes[i], Is.EqualTo(0), $"block-tip byte at {i} must be zero after wipe");
  }
}
