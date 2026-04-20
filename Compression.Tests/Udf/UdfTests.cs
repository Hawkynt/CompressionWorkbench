using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Udf;

[TestFixture]
public class UdfTests {

  /// <summary>
  /// Builds a minimal synthetic UDF image with Volume Recognition Sequence,
  /// AVDP, Partition Descriptor, Logical Volume Descriptor, FSD, root directory FE,
  /// and file entries.
  /// </summary>
  private static byte[] BuildMinimalUdf(params (string Name, byte[] Data)[] files) {
    const int sectorSize = 2048;

    // Layout:
    // Sector 16: BEA01
    // Sector 17: NSR03
    // Sector 18: TEA01
    // Sectors 32-35: VDS (Partition Desc at 32, LVD at 33, Terminator at 34)
    // Sector 256: AVDP
    // Sector 260: File Set Descriptor
    // Sector 261: Root directory File Entry
    // Sector 262: Root directory data (FIDs)
    // Sector 263+: file entries + file data

    var fileSectors = 263;
    // Each file: 1 sector for FE + ceil(size/sector) for data
    foreach (var (_, data) in files)
      fileSectors += 1 + Math.Max(1, (data.Length + sectorSize - 1) / sectorSize);

    var imageSize = Math.Max(fileSectors + 1, 300) * sectorSize;
    var img = new byte[imageSize];

    // Sector 16: BEA01
    img[16 * sectorSize] = 0; // type
    "BEA01"u8.CopyTo(img.AsSpan(16 * sectorSize + 1));

    // Sector 17: NSR03
    img[17 * sectorSize] = 0;
    "NSR03"u8.CopyTo(img.AsSpan(17 * sectorSize + 1));

    // Sector 18: TEA01
    img[18 * sectorSize] = 0;
    "TEA01"u8.CopyTo(img.AsSpan(18 * sectorSize + 1));

    // Partition start: sector 256 (simplify: everything relative to sector 256)
    // Actually let's use partition start = 258 so FSD and dirs are in partition
    var partitionStartSector = 258;

    // AVDP at sector 256
    var avdp = img.AsSpan(256 * sectorSize);
    BinaryPrimitives.WriteUInt16LittleEndian(avdp, 2); // tag ID = AVDP
    // Main VDS extent: length at offset 16, location at offset 20
    BinaryPrimitives.WriteUInt32LittleEndian(avdp[16..], 4 * (uint)sectorSize); // 4 sectors
    BinaryPrimitives.WriteUInt32LittleEndian(avdp[20..], 32); // starts at sector 32

    // Partition Descriptor at sector 32 (tag 5)
    var pd = img.AsSpan(32 * sectorSize);
    BinaryPrimitives.WriteUInt16LittleEndian(pd, 5);
    BinaryPrimitives.WriteUInt32LittleEndian(pd[188..], (uint)partitionStartSector); // partition start
    BinaryPrimitives.WriteUInt32LittleEndian(pd[192..], 1000); // partition length

    // Logical Volume Descriptor at sector 33 (tag 6)
    var lvd = img.AsSpan(33 * sectorSize);
    BinaryPrimitives.WriteUInt16LittleEndian(lvd, 6);
    BinaryPrimitives.WriteUInt32LittleEndian(lvd[212..], (uint)sectorSize); // block size
    // FSD location (long_ad): LBN at offset 252, partition ref at offset 256
    BinaryPrimitives.WriteUInt32LittleEndian(lvd[248..], (uint)sectorSize); // length
    BinaryPrimitives.WriteUInt32LittleEndian(lvd[252..], 0); // LBN 0 relative to partition

    // Terminator at sector 34
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(34 * sectorSize), 8);

    // FSD at partition LBN 0 (= sector 258)
    var fsdOff = partitionStartSector * sectorSize;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fsdOff), 256); // tag = FSD
    // Root ICB: long_ad at offset 400 (len at 400, LBN at 404)
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fsdOff + 400), (uint)sectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fsdOff + 404), 1); // LBN 1 in partition

    // Root directory File Entry at partition LBN 1 (= sector 259)
    var rootFeOff = (partitionStartSector + 1) * sectorSize;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(rootFeOff), 261); // tag = File Entry
    img[rootFeOff + 27] = 4; // file type = directory
    // ICB flags: short_ad (type 0)
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(rootFeOff + 34), 0);

    // Build FID data for root directory
    var rootDirDataLbn = 2; // partition LBN 2 = sector 260
    using var fidStream = new MemoryStream();
    var fileEntryLbn = 3; // partition LBN 3+ for file FEs

    foreach (var (name, data) in files) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      var fidIdLen = nameBytes.Length + 1; // +1 for encoding byte (8 = UTF-8)
      var fidLen = 38 + fidIdLen;
      fidLen = (fidLen + 3) & ~3; // pad to 4

      var fid = new byte[fidLen];
      BinaryPrimitives.WriteUInt16LittleEndian(fid, 257); // tag = FID
      fid[18] = 0; // flags (not parent, not deleted, file)
      fid[19] = (byte)fidIdLen; // file identifier length
      // ICB: length at 20, LBN at 24
      BinaryPrimitives.WriteUInt32LittleEndian(fid.AsSpan(20), (uint)sectorSize);
      BinaryPrimitives.WriteUInt32LittleEndian(fid.AsSpan(24), (uint)fileEntryLbn);
      // L_IU at offset 36
      BinaryPrimitives.WriteUInt16LittleEndian(fid.AsSpan(36), 0);
      // File identifier at offset 38
      fid[38] = 8; // OSTA CS0 = UTF-8
      nameBytes.CopyTo(fid, 39);
      fidStream.Write(fid);

      // Write file FE at fileEntryLbn
      var feOff = (partitionStartSector + fileEntryLbn) * sectorSize;
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(feOff), 261); // File Entry
      img[feOff + 27] = 5; // file type = regular file
      BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(feOff + 56), (ulong)data.Length); // info length

      var dataLbn = fileEntryLbn + 1;
      var dataSectors = Math.Max(1, (data.Length + sectorSize - 1) / sectorSize);

      // Short alloc descriptor at offset 176 (L_EA=0)
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(feOff + 34), 0); // ICB flags = short_ad
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(feOff + 168), 0); // L_EA
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(feOff + 172), 8); // L_AD = 8 (one short_ad)
      // Short AD: length + position
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(feOff + 176), (uint)data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(feOff + 180), (uint)dataLbn);

      // Write file data
      var dataOff = (partitionStartSector + dataLbn) * sectorSize;
      if (data.Length > 0 && dataOff + data.Length <= img.Length)
        data.CopyTo(img, dataOff);

      fileEntryLbn += 1 + dataSectors;
    }

    // Write FID data at rootDirDataLbn
    var fidData = fidStream.ToArray();
    var fidDataOff = (partitionStartSector + rootDirDataLbn) * sectorSize;
    if (fidData.Length > 0 && fidDataOff + fidData.Length <= img.Length)
      fidData.CopyTo(img, fidDataOff);

    // Root FE: info length and alloc descriptor pointing to FID data
    BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(rootFeOff + 56), (ulong)fidData.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootFeOff + 168), 0); // L_EA = 0
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootFeOff + 172), 8); // L_AD = 8
    // Short AD: length + position
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootFeOff + 176), (uint)fidData.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootFeOff + 180), (uint)rootDirDataLbn);

    return img;
  }

  [Test, Category("HappyPath")]
  public void Read_SingleFile() {
    var content = "Hello UDF!"u8.ToArray();
    var img = BuildMinimalUdf(("test.txt", content));
    using var ms = new MemoryStream(img);

    var r = new FileFormat.Udf.UdfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));
  }

  [Test, Category("HappyPath")]
  public void Extract_SingleFile() {
    var content = "Hello UDF!"u8.ToArray();
    var img = BuildMinimalUdf(("test.txt", content));
    using var ms = new MemoryStream(img);

    var r = new FileFormat.Udf.UdfReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleFiles() {
    var img = BuildMinimalUdf(("a.txt", "First"u8.ToArray()), ("b.txt", "Second"u8.ToArray()));
    using var ms = new MemoryStream(img);

    var r = new FileFormat.Udf.UdfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Udf.UdfFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Udf"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".udf"));
    Assert.That(desc.Extensions, Does.Contain(".udf"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Udf.UdfReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_NoNsr_Throws() {
    var data = new byte[300 * 2048];
    // Write AVDP tag at sector 256 but no NSR
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(256 * 2048), 2);
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Udf.UdfReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var img = BuildMinimalUdf(("file.bin", new byte[10]));
    using var ms = new MemoryStream(img);
    var desc = new FileFormat.Udf.UdfFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  // ── WORM creation ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Udf.UdfFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = "hello udf world"u8.ToArray();
    var w = new FileFormat.Udf.UdfWriter();
    w.AddFile("readme.txt", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Udf.UdfReader(ms);
    Assert.That(r.Entries.Count(e => !e.IsDirectory), Is.EqualTo(1));
    var entry = r.Entries.First(e => !e.IsDirectory);
    Assert.That(entry.Name, Is.EqualTo("readme.txt"));
    Assert.That(entry.Size, Is.EqualTo(payload.Length));
    Assert.That(r.Extract(entry), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_MultipleFiles_AllRoundTrip() {
    var p1 = new byte[100];
    var p2 = new byte[300];
    new Random(1).NextBytes(p1);
    new Random(2).NextBytes(p2);

    var w = new FileFormat.Udf.UdfWriter();
    w.AddFile("a.bin", p1);
    w.AddFile("b.bin", p2);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Udf.UdfReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(2));
    Assert.That(r.Extract(files.First(e => e.Name == "a.bin")), Is.EqualTo(p1));
    Assert.That(r.Extract(files.First(e => e.Name == "b.bin")), Is.EqualTo(p2));
  }

  [Test, Category("HappyPath")]
  public void Writer_HasNsrMagic() {
    var w = new FileFormat.Udf.UdfWriter();
    w.AddFile("x.txt", "x"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    // NSR02 at sector 17, offset 1
    Assert.That(Encoding.ASCII.GetString(bytes, 17 * 2048 + 1, 5), Is.EqualTo("NSR02"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "udf descriptor test"u8.ToArray());
      var d = new FileFormat.Udf.UdfFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "test.txt", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name), Has.Member("test.txt"));
    } finally {
      File.Delete(tmp);
    }
  }
}
