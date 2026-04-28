using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Ext1;

namespace Compression.Tests.Ext1;

/// <summary>
/// Unit tests for <see cref="Ext1Writer"/> — verifies the rev-0 / GOOD_OLD_REV
/// invariants that distinguish ext1 from ext2:
/// magic <c>0xEF51</c>, <c>s_rev_level=0</c>, 128-byte inodes, and rev-0 dirent
/// layout (16-bit <c>name_len</c>, no <c>file_type</c> byte).
/// </summary>
[TestFixture]
public class Ext1WriterTests {

  private static byte[] BuildImageWithFiles(params (string Name, byte[] Data)[] files) {
    var w = new Ext1Writer();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  [Test, Category("HappyPath")]
  public void Writer_RoundTrip_OurReader() {
    var hello = "Hello ext1!"u8.ToArray();
    var goodbye = "Goodbye, brave new ext2."u8.ToArray();
    var img = BuildImageWithFiles(("hello.txt", hello), ("goodbye.txt", goodbye));

    using var ms = new MemoryStream(img);
    using var reader = new Ext1Reader(ms);
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);

    Assert.That(entries.ContainsKey("hello.txt"), Is.True, "hello.txt should appear in directory listing");
    Assert.That(entries.ContainsKey("goodbye.txt"), Is.True, "goodbye.txt should appear in directory listing");
    Assert.That(reader.Extract(entries["hello.txt"]), Is.EqualTo(hello));
    Assert.That(reader.Extract(entries["goodbye.txt"]), Is.EqualTo(goodbye));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_RoundTrip_ExtractWritesFile() {
    var hello = "round-trip via descriptor"u8.ToArray();
    var img = BuildImageWithFiles(("hello.txt", hello));

    var d = new Ext1FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "ext1w_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, outDir, null, null);
      var helloPath = Path.Combine(outDir, "hello.txt");
      Assert.That(File.Exists(helloPath), Is.True, "extractor should drop hello.txt next to FULL.ext1");
      Assert.That(File.ReadAllBytes(helloPath), Is.EqualTo(hello));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Writer_SuperblockMagicIs0xEF51() {
    var img = BuildImageWithFiles(("a.txt", "x"u8.ToArray()));
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(1080, 2));
    Assert.That(magic, Is.EqualTo((ushort)0xEF51), "ext1 magic must be 0xEF51 (not ext2's 0xEF53)");
  }

  [Test, Category("HappyPath")]
  public void Writer_RevLevelIs0() {
    var img = BuildImageWithFiles(("a.txt", "x"u8.ToArray()));
    // s_rev_level is at file offset 1024 + 0x4C (76) = 1100.
    var revLevel = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(1024 + 76, 4));
    Assert.That(revLevel, Is.EqualTo(0u), "ext1 must use GOOD_OLD_REV (rev_level=0), not DYNAMIC_REV (=1)");
  }

  [Test, Category("HappyPath")]
  public void Writer_InodeSizeIs128Bytes() {
    var img = BuildImageWithFiles(("a.txt", "x"u8.ToArray()));
    // Rev-0 does not store s_inode_size — the field at SB offset 88 should be 0
    // (and consumers must default to 128 for GOOD_OLD_REV).
    var sInodeSize = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(1024 + 88, 2));
    Assert.That(sInodeSize, Is.EqualTo((ushort)0), "rev-0 must NOT populate s_inode_size; tooling defaults to 128");

    // Indirectly verify 128-byte inode stride: write 2 files and check that the
    // inode_table block layout places file inodes at 11 and 12 (= bytes
    // 10*128 and 11*128 from inode_table start).
    var img2 = BuildImageWithFiles(("a.txt", "Aaa"u8.ToArray()), ("b.txt", "Bbb"u8.ToArray()));
    // inode_table is at block (firstDataBlock+4) = block 5 with 1 KiB blocks =
    // file offset 5*1024 = 5120.
    var inode11 = img2.AsSpan(5120 + 10 * 128, 128);
    var inode12 = img2.AsSpan(5120 + 11 * 128, 128);
    var size11 = BinaryPrimitives.ReadUInt32LittleEndian(inode11.Slice(4, 4));
    var size12 = BinaryPrimitives.ReadUInt32LittleEndian(inode12.Slice(4, 4));
    Assert.That(size11, Is.EqualTo(3u), "inode 11 i_size should be 3 (= len('Aaa')) at 128-byte stride");
    Assert.That(size12, Is.EqualTo(3u), "inode 12 i_size should be 3 (= len('Bbb')) at 128-byte stride");
  }

  [Test, Category("HappyPath")]
  public void Writer_DirentHasNoFileTypeByte() {
    var img = BuildImageWithFiles(("hello.txt", "h"u8.ToArray()));

    // Locate the root directory block. Root inode is inode 2 → at inode_table
    // offset (block 5 * 1024) + 1*128 = 5248. The first direct block pointer
    // is at inode offset 40.
    var rootInode = img.AsSpan(5120 + 1 * 128, 128);
    var rootBlock = BinaryPrimitives.ReadUInt32LittleEndian(rootInode.Slice(40, 4));
    Assert.That(rootBlock, Is.GreaterThan(5u), "root dir block must lie past the metadata blocks");

    var dir = img.AsSpan((int)rootBlock * 1024, 1024);

    // Walk to the third entry — first is "." (rec_len 12), second is ".." (rec_len 12).
    var pos = 0;
    pos += BinaryPrimitives.ReadUInt16LittleEndian(dir.Slice(pos + 4, 2));
    pos += BinaryPrimitives.ReadUInt16LittleEndian(dir.Slice(pos + 4, 2));

    var inodeNum = BinaryPrimitives.ReadUInt32LittleEndian(dir.Slice(pos, 4));
    var nameLen16 = BinaryPrimitives.ReadUInt16LittleEndian(dir.Slice(pos + 6, 2));
    var name = Encoding.UTF8.GetString(dir.Slice(pos + 8, nameLen16));

    Assert.That(inodeNum, Is.EqualTo(11u), "first user file should be inode 11");
    Assert.That(nameLen16, Is.EqualTo(9), "name_len for 'hello.txt' should be 9 read as a 16-bit field");
    Assert.That(name, Is.EqualTo("hello.txt"));

    // If we treated offset+7 as a file_type byte (rev-1 layout), it would be 0
    // for an entry with name_len=9. With rev-0 the byte at offset+7 is the
    // upper 8 bits of name_len, which for ASCII-length names is *also* 0 —
    // but if we picked a name with more than 255 bytes (impossible in practice)
    // those upper bits would be non-zero. The reliable assertion is structural:
    // re-read the same field as 8-bit name_len + 8-bit file_type and verify
    // the file_type slot is 0 (i.e. there is no FILETYPE marker), proving we
    // emitted rev-0 layout.
    var nameLen8 = dir[pos + 6];
    var fileTypeSlot = dir[pos + 7];
    Assert.That(nameLen8, Is.EqualTo(9), "low byte of name_len must equal the actual name length");
    Assert.That(fileTypeSlot, Is.EqualTo(0),
      "rev-0 dirent must NOT emit a file_type byte; the slot is the high byte of 16-bit name_len which is 0 here");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListIncludesUserFiles() {
    var img = BuildImageWithFiles(("hello.txt", "abc"u8.ToArray()), ("readme.md", "def"u8.ToArray()));
    var d = new Ext1FormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("hello.txt"));
    Assert.That(names, Does.Contain("readme.md"));
    Assert.That(names, Does.Contain("FULL.ext1"));
    Assert.That(names, Does.Contain("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_HasCanCreateCapability() {
    var d = new Ext1FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_CreateThenList_RoundTrips() {
    var tmp = Path.Combine(Path.GetTempPath(), "ext1create_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      var src1 = Path.Combine(tmp, "alpha.txt");
      var src2 = Path.Combine(tmp, "beta.txt");
      File.WriteAllBytes(src1, "ALPHA"u8.ToArray());
      File.WriteAllBytes(src2, "BETA"u8.ToArray());

      var inputs = new List<ArchiveInputInfo> {
        new(src1, "alpha.txt", false),
        new(src2, "beta.txt", false),
      };

      var d = new Ext1FormatDescriptor();
      using var outMs = new MemoryStream();
      d.Create(outMs, inputs, new FormatCreateOptions());

      outMs.Position = 0;
      var entries = d.List(outMs, null);
      var names = entries.Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("alpha.txt"));
      Assert.That(names, Does.Contain("beta.txt"));
    } finally {
      try { Directory.Delete(tmp, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Writer_WriteTo_StreamMatchesBuild() {
    var w = new Ext1Writer();
    w.AddFile("a.txt", "A"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    Assert.That(ms.Length, Is.EqualTo(4 * 1024 * 1024), "default WriteTo should emit a 4 MiB image");
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(ms.GetBuffer().AsSpan(1080, 2));
    Assert.That(magic, Is.EqualTo((ushort)0xEF51));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_FileTooLarge_Throws() {
    var w = new Ext1Writer();
    var huge = new byte[13 * 1024]; // 13 KiB > 12 direct blocks × 1 KiB
    w.AddFile("huge.bin", huge);
    Assert.Throws<InvalidOperationException>(() => w.Build());
  }

  [Test, Category("HappyPath")]
  public void WriteConstraints_RejectDirectoryAndOversize() {
    var d = new Ext1FormatDescriptor();
    // Directory entry is rejected.
    var dirInput = new ArchiveInputInfo("/tmp/dir", "dir", true);
    Assert.That(d.CanAccept(dirInput, out var dirReason), Is.False);
    Assert.That(dirReason, Does.Contain("flat WORM").IgnoreCase.Or.Contain("directory").IgnoreCase);

    // Oversize file is rejected.
    var tmp = Path.Combine(Path.GetTempPath(), "ext1cons_" + Guid.NewGuid().ToString("N") + ".bin");
    File.WriteAllBytes(tmp, new byte[13 * 1024]);
    try {
      var input = new ArchiveInputInfo(tmp, "huge.bin", false);
      Assert.That(d.CanAccept(input, out var reason), Is.False);
      Assert.That(reason, Does.Contain("12 KiB"));
    } finally {
      try { File.Delete(tmp); } catch { /* ignore */ }
    }
  }
}
