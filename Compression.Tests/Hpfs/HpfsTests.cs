using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Hpfs;

namespace Compression.Tests.Hpfs;

[TestFixture]
public class HpfsTests {

  private const int LbaSize = HpfsReader.LbaSize;  // 512
  private const int DirBlockSize = HpfsReader.DirBlockSize;  // 2048

  /// <summary>
  /// Builds a minimal HPFS image containing:
  ///   - Superblock at LBA 16, pointing at a root fnode at LBA 17.
  ///   - Root fnode at LBA 17, whose first direct-allocation entry points at the
  ///     root directory block at LBA 32.
  ///   - One regular-file dirent at LBA 32, whose fnode is at LBA 40 and whose data
  ///     is at LBA 48 (one LBA, containing <paramref name="payload"/>).
  /// </summary>
  private static byte[] BuildMinimalHpfs(string fileName, byte[] payload) {
    const uint RootFnodeLba = 17;
    const uint RootDirLba = 32;
    const uint FileFnodeLba = 40;
    const uint FileDataLba = 48;

    // Allocate 128 LBAs worth of space.
    var img = new byte[LbaSize * 128];

    // --- Superblock at LBA 16 ---
    var sbOff = (int)HpfsReader.SuperblockLba * LbaSize;
    Buffer.BlockCopy(HpfsReader.SuperblockMagic, 0, img, sbOff, HpfsReader.SuperblockMagic.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(sbOff + 12), RootFnodeLba);

    // --- Root fnode at LBA 17 ---
    var rootFnode = (int)RootFnodeLba * LbaSize;
    Buffer.BlockCopy(HpfsReader.FnodeMagic, 0, img, rootFnode, HpfsReader.FnodeMagic.Length);
    // AllocSec header at 0xC0: height=0 (direct list). Leave 0.
    // First direct-allocation entry at 0xC4: [4:logicalSec][4:length][4:physicalLba].
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootFnode + 0xC4 + 0), 0u);             // logicalSec
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootFnode + 0xC4 + 4), 4u);             // length (4 LBAs = 2 KiB)
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootFnode + 0xC4 + 8), RootDirLba);     // physicalLba

    // --- Root directory block at LBA 32 ---
    var dirOff = (int)RootDirLba * LbaSize;
    Buffer.BlockCopy(HpfsReader.DirBlockMagic, 0, img, dirOff, HpfsReader.DirBlockMagic.Length);

    // First dirent at offset 0x14.
    var direntOff = dirOff + 0x14;
    var nameBytes = Encoding.Latin1.GetBytes(fileName);
    var recLen = (ushort)(32 + nameBytes.Length);  // header 32 + name
    // Round up to a multiple of 4 for alignment.
    if ((recLen & 3) != 0) recLen = (ushort)((recLen + 3) & ~3);

    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(direntOff + 0), recLen);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(direntOff + 2), 0);  // flags: regular file
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(direntOff + 4), FileFnodeLba);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(direntOff + 12), (uint)payload.Length);
    img[direntOff + 30] = (byte)nameBytes.Length;
    Buffer.BlockCopy(nameBytes, 0, img, direntOff + 31, nameBytes.Length);

    // End-of-block sentinel dirent immediately after.
    var endOff = direntOff + recLen;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(endOff + 0), 32);     // min length
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(endOff + 2), 0x0001); // "special" flag

    // --- File fnode at LBA 40 ---
    var fileFnode = (int)FileFnodeLba * LbaSize;
    Buffer.BlockCopy(HpfsReader.FnodeMagic, 0, img, fileFnode, HpfsReader.FnodeMagic.Length);
    // AllocSec header height=0 (direct list).
    // First direct-allocation entry: points at file's data LBA.
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fileFnode + 0xC4 + 0), 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fileFnode + 0xC4 + 4), 1u);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fileFnode + 0xC4 + 8), FileDataLba);

    // --- File data at LBA 48 ---
    var dataOff = (int)FileDataLba * LbaSize;
    Buffer.BlockCopy(payload, 0, img, dataOff, payload.Length);

    return img;
  }

  [Test, Category("HappyPath")]
  public void Reader_ListsSingleFile() {
    var payload = Encoding.ASCII.GetBytes("HPFS payload");
    var img = BuildMinimalHpfs("CONFIG.SYS", payload);

    using var r = new HpfsReader(new MemoryStream(img));
    Assert.That(r.RootFnodeLba, Is.EqualTo(17u));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("CONFIG.SYS"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(r.Entries[0].IsDirectory, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsSmallFile() {
    var payload = Encoding.ASCII.GetBytes("OS/2 Warp 4 readme");
    var img = BuildMinimalHpfs("README.TXT", payload);

    using var r = new HpfsReader(new MemoryStream(img));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new HpfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Hpfs"));
    Assert.That(d.Extensions, Does.Contain(".img"));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThan(0));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(8192));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var img = BuildMinimalHpfs("TEST.DAT", "data"u8.ToArray());
    var d = new HpfsFormatDescriptor();
    var entries = d.List(new MemoryStream(img), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("TEST.DAT"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_MissingMagic_Throws() {
    var img = new byte[LbaSize * 32];  // no magic at LBA 16
    Assert.Throws<InvalidDataException>(() =>
      _ = new HpfsReader(new MemoryStream(img)));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    Assert.Throws<InvalidDataException>(() =>
      _ = new HpfsReader(new MemoryStream(new byte[1024])));
  }

  // ── Capability checks (replaces Descriptor_IsHonestlyReadOnly) ─────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsCreatable() {
    var d = new HpfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsModifiable() {
    var d = new HpfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsDefragmentable() {
    var d = new HpfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsExtentMap() {
    var d = new HpfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_SupportsListExtractTest() {
    var d = new HpfsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
  }

  // ── Writer round-trip tests ────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = "Hello HPFS!"u8.ToArray();
    var w = new HpfsWriter();
    w.AddFile("TEST.TXT", payload);
    var image = w.Build();

    using var r = new HpfsReader(new MemoryStream(image));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_ThreeFiles_RoundTrip() {
    var w = new HpfsWriter();
    var file1 = "alpha content"u8.ToArray();
    var file2 = new byte[600]; // > 1 LBA
    for (var i = 0; i < file2.Length; i++) file2[i] = (byte)(i & 0xFF);
    var file3 = "tiny"u8.ToArray();

    w.AddFile("ALPHA.TXT", file1);
    w.AddFile("BIGFILE.BIN", file2);
    w.AddFile("SMALL.DAT", file3);
    var image = w.Build();

    using var r = new HpfsReader(new MemoryStream(image));
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName.ContainsKey("ALPHA.TXT"), Is.True);
    Assert.That(byName.ContainsKey("BIGFILE.BIN"), Is.True);
    Assert.That(byName.ContainsKey("SMALL.DAT"), Is.True);

    Assert.That(r.Extract(byName["ALPHA.TXT"]), Is.EqualTo(file1));
    Assert.That(r.Extract(byName["BIGFILE.BIN"]), Is.EqualTo(file2));
    Assert.That(r.Extract(byName["SMALL.DAT"]), Is.EqualTo(file3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_EmptyFile_RoundTrips() {
    var w = new HpfsWriter();
    w.AddFile("EMPTY.TXT", []);
    var image = w.Build();

    using var r = new HpfsReader(new MemoryStream(image));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Writer_SuperblockHasValidMagic() {
    var w = new HpfsWriter();
    w.AddFile("X.TXT", "data"u8.ToArray());
    var image = w.Build();

    // Superblock at LBA 16
    var sbOff = HpfsReader.SuperblockLba * LbaSize;
    Assert.That(image[sbOff..][..HpfsReader.SuperblockMagic.Length],
      Is.EqualTo(HpfsReader.SuperblockMagic));
  }

  // ── Descriptor Create round-trip ───────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp1 = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp1, "first file"u8.ToArray());
      File.WriteAllBytes(tmp2, "second file"u8.ToArray());

      var inputs = new List<ArchiveInputInfo> {
        new(tmp1, "README.TXT", false),
        new(tmp2, "DATA.BIN", false),
      };

      var d = new HpfsFormatDescriptor();
      using var ms = new MemoryStream();
      d.Create(ms, inputs, new FormatCreateOptions());
      ms.Position = 0;

      var entries = d.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(2));
      var names = entries.Select(e => e.Name).ToHashSet();
      Assert.That(names, Does.Contain("README.TXT"));
      Assert.That(names, Does.Contain("DATA.BIN"));

      ms.Position = 0;
      var outDir = Path.Combine(Path.GetTempPath(), "hpfs_create_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(outDir);
      try {
        d.Extract(ms, outDir, null, null);
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, "README.TXT")),
          Is.EqualTo("first file"u8.ToArray()));
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, "DATA.BIN")),
          Is.EqualTo("second file"u8.ToArray()));
      } finally {
        try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
      }
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
    }
  }

  // ── Modify (Add/Remove) tests ─────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Modify_Add_AppendsFile() {
    var w = new HpfsWriter();
    w.AddFile("ORIGINAL.TXT", "original"u8.ToArray());
    var image = w.Build();

    var d = new HpfsFormatDescriptor();
    using var ms = new MemoryStream(image);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added content"u8.ToArray());
      d.Add(ms, [new ArchiveInputInfo(tmp, "ADDED.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("ORIGINAL.TXT"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("ADDED.TXT"));
  }

  [Test, Category("HappyPath")]
  public void Modify_Remove_RemovesFile() {
    var w = new HpfsWriter();
    w.AddFile("KEEP.TXT", "keep me"u8.ToArray());
    w.AddFile("DELETE.TXT", "remove me"u8.ToArray());
    var image = w.Build();

    var d = new HpfsFormatDescriptor();
    using var ms = new MemoryStream(image);
    d.Remove(ms, ["DELETE.TXT"]);

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("KEEP.TXT"));
    Assert.That(entries.Select(e => e.Name), Does.Not.Contain("DELETE.TXT"));
  }

  // ── Extent map test ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ExtentMap_ReturnsMetadataAndFileExtents() {
    var w = new HpfsWriter();
    w.AddFile("A.TXT", "some data"u8.ToArray());
    w.AddFile("B.TXT", new byte[1024]);
    var image = w.Build();

    var d = new HpfsFormatDescriptor();
    using var ms = new MemoryStream(image);
    var extents = d.EnumerateExtents(ms).ToList();

    Assert.That(extents.Where(e => e.Kind == DefragBlockKind.MetadataReserved), Is.Not.Empty,
      "Expected metadata-reserved regions");
    var fileExtents = extents.Where(e => e.Kind == DefragBlockKind.Used).ToList();
    Assert.That(fileExtents, Is.Not.Empty, "Expected file data regions");
  }

  // ── Defrag test ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Defragment_PreservesAllFiles() {
    var w = new HpfsWriter();
    w.AddFile("ONE.TXT", "first"u8.ToArray());
    w.AddFile("TWO.TXT", "second"u8.ToArray());
    var image = w.Build();

    var d = new HpfsFormatDescriptor();
    using var ms = new MemoryStream(image);
    d.Defragment(ms);

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("ONE.TXT"));
    Assert.That(names, Does.Contain("TWO.TXT"));
  }
}
