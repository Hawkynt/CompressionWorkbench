using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Xfs;

/// <summary>
/// Behaviour: zeroing unused space in an XFS image must leave live file data
/// intact while scrubbing the tail slack of the last data block (cluster tip)
/// and any free blocks. XFS stores file data in block-aligned extents; a file
/// shorter than its last block leaves trailing slack inside that block.
/// </summary>
[TestFixture]
public class XfsWipeEmptyTests {

  private const uint BlockSize = 4096;
  private const ushort InodeSize = 256;
  private const uint AgBlocks = 256;
  private const int InoPerBlock = (int)(BlockSize / InodeSize); // 16

  /// <summary>
  /// Builds a one-file XFS v4 image where the single file is stored in
  /// extents format (data block) and is shorter than a full block, so its
  /// last data block has trailing tip slack. Returns the byte offset and
  /// length of that tip slack.
  /// </summary>
  private static byte[] BuildImageWithTip(byte[] content, out long tipOffset, out long tipLength, out long dataBlockOffset) {
    var agBlkLog = 8; // log2(256)
    var imageSize = (int)(AgBlocks * BlockSize);
    var img = new byte[imageSize];

    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0), 0x58465342);   // XFSB
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(4), BlockSize);
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(8), AgBlocks);
    var rootIno = (ulong)(4 * InoPerBlock);
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(56), rootIno);
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(84), AgBlocks);
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(88), 1);
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(100), 4);          // v4
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(104), InodeSize);
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(106), InoPerBlock);
    img[124] = (byte)agBlkLog;

    var rootOff = 4 * (int)BlockSize;
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(rootOff), 0x494E);
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(rootOff + 2), 0x41ED);
    img[rootOff + 4] = 2;
    img[rootOff + 5] = 1; // short-form dir
    var sfOff = rootOff + 100;
    img[sfOff] = 1; // one entry
    img[sfOff + 1] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(sfOff + 2), (uint)rootIno);
    var entryPos = sfOff + 6;

    var name = "tip.bin";
    var nameBytes = Encoding.UTF8.GetBytes(name);
    // Place the file inode and data block beyond block 8 so they fall outside
    // the per-AG metadata tile (first 8 blocks) the extent map reserves.
    const int fileBlock = 12;
    var fileIno = (ulong)(fileBlock * InoPerBlock);
    img[entryPos] = (byte)nameBytes.Length;
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(entryPos + 1), 3);
    nameBytes.CopyTo(img, entryPos + 3);
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(entryPos + 3 + nameBytes.Length), (uint)fileIno);
    entryPos += 3 + nameBytes.Length + 4;
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(rootOff + 56), (ulong)(entryPos - sfOff));

    // File inode at block 12, data block 13 (extents format).
    var fInodeOff = fileBlock * (int)BlockSize;
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(fInodeOff), 0x494E);
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(fInodeOff + 2), 0x81A4); // S_IFREG
    img[fInodeOff + 4] = 2;
    img[fInodeOff + 5] = 2; // extents
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 56), (ulong)content.Length);
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(fInodeOff + 76), 1); // 1 extent

    var dataBlock = fileBlock + 1;
    var dataBlocks = (content.Length + (int)BlockSize - 1) / (int)BlockSize;
    ulong startBlock = (ulong)dataBlock;
    ulong hi = (startBlock >> 43) & 0x1FF;
    ulong lo = ((startBlock & 0x7FFFFFFFFFF) << 21) | (uint)dataBlocks;
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 100), hi);
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 108), lo);

    dataBlockOffset = dataBlock * (long)BlockSize;
    content.CopyTo(img, (int)dataBlockOffset);

    tipOffset = dataBlockOffset + content.Length;
    tipLength = (long)dataBlocks * BlockSize - content.Length;
    return img;
  }

  [Test, Category("HappyPath"), Category("WipeEmpty")]
  public void WipeUnusedSpace_ZeroesClusterTip_AndPreservesFile() {
    // Given a file shorter than one 4 KiB block, with dirtied tip slack.
    var content = new byte[200];
    Array.Fill(content, (byte)0xAA);
    var img = BuildImageWithTip(content, out var tipOffset, out var tipLength, out var dataBlockOffset);

    // Dirty the tip slack so the wipe has something to scrub.
    for (var i = tipOffset; i < tipOffset + tipLength; i++) img[(int)i] = 0xBB;

    using var ms = new MemoryStream();
    ms.Write(img);
    ms.Position = 0;

    var d = new FileSystem.Xfs.XfsFormatDescriptor();

    // When the unused space is wiped (cluster tips included).
    var wiped = ((IWipeEmpty)d).WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    // Then bytes were scrubbed.
    Assert.That(wiped, Is.GreaterThan(0));

    // And the file round-trips intact.
    ms.Position = 0;
    var r = new FileSystem.Xfs.XfsReader(ms);
    var entry = r.Entries.First(e => e.Name == "tip.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(content), "file content must survive the wipe");

    // And the cluster tip is now all zero.
    ms.Position = 0;
    var buf = ms.ToArray();
    for (var i = tipOffset; i < tipOffset + tipLength; i++)
      Assert.That(buf[(int)i], Is.EqualTo(0), $"tip byte at {i} must be zeroed");
  }
}
