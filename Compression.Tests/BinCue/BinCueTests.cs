namespace Compression.Tests.BinCue;

[TestFixture]
public class BinCueTests {

  // Builds a minimal ISO 9660 image as a flat 2048-byte-sector stream
  // containing the files specified.
  private static byte[] BuildIso(params (string Name, byte[] Data)[] files) {
    // Allocate a fixed-size buffer covering: system area (16 sectors) +
    // PVD (1) + PVD terminator (1) + root directory (1) + file data sectors.
    const int sectorSize = 2048;
    const int pvdLba = 16;
    const int pvdTermLba = 17;
    const int rootDirLba = 18;
    var firstFileLba = 19;

    // Calculate total sectors needed
    var totalSectors = firstFileLba + files.Length + 1;
    var buf = new byte[totalSectors * sectorSize];

    // Helper: write bytes at absolute byte offset
    void Write(int offset, ReadOnlySpan<byte> data) => data.CopyTo(buf.AsSpan(offset));

    // ---- Build root directory records ----
    // Each directory record: length, ext_attr_len, LBA(8), size(8), date(7), flags, unit_size, gap_size, vol_seq(4), id_len, id
    var dirSectorOffset = rootDirLba * sectorSize;
    var dirPos = dirSectorOffset;

    // Dot entry (.)
    var dotRecord = new byte[34];
    dotRecord[0] = 34;       // length
    Array.Copy(BitConverter.GetBytes((uint)rootDirLba), 0, dotRecord, 2, 4); // LBA LE
    Array.Copy(BitConverter.GetBytes((uint)rootDirLba), 0, dotRecord, 6, 4); // LBA BE reversed
    dotRecord[25] = 0x02;    // directory flag
    dotRecord[32] = 1;       // id length
    dotRecord[33] = 0x00;    // dot identifier
    Write(dirPos, dotRecord);
    dirPos += 34;

    // Dotdot entry (..)
    var dotdotRecord = new byte[34];
    dotdotRecord[0] = 34;
    Array.Copy(BitConverter.GetBytes((uint)rootDirLba), 0, dotdotRecord, 2, 4);
    Array.Copy(BitConverter.GetBytes((uint)rootDirLba), 0, dotdotRecord, 6, 4);
    dotdotRecord[25] = 0x02;
    dotdotRecord[32] = 1;
    dotdotRecord[33] = 0x01; // dotdot identifier
    Write(dirPos, dotdotRecord);
    dirPos += 34;

    // File entries
    var fileLbas = new int[files.Length];
    for (var i = 0; i < files.Length; i++) {
      var (name, data) = files[i];
      var lba = firstFileLba + i;
      fileLbas[i] = lba;

      // Write file data into its sector
      Write(lba * sectorSize, data);

      // Build ISO 9660 file name: uppercase + ";1"
      var isoName = name.ToUpperInvariant() + ";1";
      var idLen = (byte)isoName.Length;
      var recLen = (byte)(33 + idLen);
      if ((recLen & 1) != 0) recLen++; // pad to even

      var rec = new byte[recLen];
      rec[0] = recLen;
      // LBA both-endian at offset 2
      rec[2] = (byte)lba; rec[3] = (byte)(lba >> 8); rec[4] = (byte)(lba >> 16); rec[5] = (byte)(lba >> 24);
      rec[6] = (byte)(lba >> 24); rec[7] = (byte)(lba >> 16); rec[8] = (byte)(lba >> 8); rec[9] = (byte)lba;
      // Size both-endian at offset 10
      var sz = (uint)data.Length;
      rec[10] = (byte)sz; rec[11] = (byte)(sz >> 8); rec[12] = (byte)(sz >> 16); rec[13] = (byte)(sz >> 24);
      rec[14] = (byte)(sz >> 24); rec[15] = (byte)(sz >> 16); rec[16] = (byte)(sz >> 8); rec[17] = (byte)sz;
      // flags at 25: 0 = file
      rec[32] = idLen;
      System.Text.Encoding.ASCII.GetBytes(isoName).CopyTo(rec, 33);

      Write(dirPos, rec);
      dirPos += recLen;
    }

    var rootDirSize = (uint)(dirPos - dirSectorOffset);

    // ---- Build PVD at LBA 16 ----
    var pvd = new byte[sectorSize];
    pvd[0] = 1;  // type: Primary Volume Descriptor
    System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1);
    pvd[6] = 1;  // version

    // Root directory record at PVD offset 156 (34 bytes)
    pvd[156] = 34;
    // LBA both-endian at 156+2
    pvd[158] = (byte)rootDirLba; pvd[162] = (byte)rootDirLba; // simplified
    // Size both-endian at 156+10
    pvd[166] = (byte)rootDirSize; pvd[170] = (byte)rootDirSize; // simplified
    pvd[156 + 25] = 0x02; // directory
    pvd[156 + 32] = 1;
    pvd[156 + 33] = 0x00;

    Write(pvdLba * sectorSize, pvd);

    // PVD terminator at LBA 17
    var pvdTerm = new byte[sectorSize];
    pvdTerm[0] = 0xFF; // type: volume descriptor set terminator
    System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(pvdTerm, 1);
    Write(pvdTermLba * sectorSize, pvdTerm);

    return buf;
  }

  // Wraps flat ISO sector data into 2352-byte raw sectors (Mode 1)
  private static byte[] WrapRaw2352(byte[] isoData) {
    const int isoSectorSize = 2048;
    const int rawSectorSize = 2352;
    const int dataOffset = 16; // Mode 1 user data offset

    var sectorCount = isoData.Length / isoSectorSize;
    var raw = new byte[sectorCount * rawSectorSize];

    for (var i = 0; i < sectorCount; i++) {
      // Write Mode 1 type byte at offset 15
      raw[i * rawSectorSize + 15] = 0x01;
      // Copy 2048 bytes of user data at offset 16
      Array.Copy(isoData, i * isoSectorSize, raw, i * rawSectorSize + dataOffset, isoSectorSize);
    }

    // Inject CD001 PVD signature so probe succeeds
    // The probe checks raw[PvdLba * 2352 + 16] for type=1 and "CD001"
    // This is already set by WrapRaw2352 copying from the ISO buffer.
    return raw;
  }

  // ---- Tests ----

  [Test, Category("HappyPath")]
  public void Read_FlatIso_ListsFiles() {
    var fileData = "Hello from ISO"u8.ToArray();
    var iso = BuildIso(("readme.txt", fileData));
    using var ms = new MemoryStream(iso);

    var r = new FileFormat.BinCue.BinCueReader(ms);

    Assert.That(r.Entries, Has.Count.GreaterThan(0));
    var fileEntry = r.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.StartsWith("README"));
    Assert.That(fileEntry, Is.Not.Null);
    Assert.That(fileEntry!.Size, Is.EqualTo(fileData.Length));
  }

  [Test, Category("HappyPath")]
  public void Read_FlatIso_ExtractFileData() {
    var fileData = new byte[512];
    Random.Shared.NextBytes(fileData);
    var iso = BuildIso(("data.bin", fileData));
    using var ms = new MemoryStream(iso);

    var r = new FileFormat.BinCue.BinCueReader(ms);
    var entry = r.Entries.FirstOrDefault(e => !e.IsDirectory);
    Assert.That(entry, Is.Not.Null);

    var extracted = r.Extract(entry!);
    Assert.That(extracted[..fileData.Length], Is.EqualTo(fileData));
  }

  [Test, Category("HappyPath")]
  public void Read_Raw2352Sectors_ListsFiles() {
    var fileData = "sector wrapped"u8.ToArray();
    var iso = BuildIso(("track.txt", fileData));
    var raw = WrapRaw2352(iso);
    using var ms = new MemoryStream(raw);

    var r = new FileFormat.BinCue.BinCueReader(ms);
    Assert.That(r.Entries, Has.Count.GreaterThan(0));
  }

  [Test, Category("HappyPath")]
  public void BinCueEntry_Properties() {
    var entry = new FileFormat.BinCue.BinCueEntry {
      Name = "FILE.TXT",
      FullPath = "DIR/FILE.TXT",
      IsDirectory = false,
      Size = 1234,
      StartLba = 25,
    };
    Assert.That(entry.Name, Is.EqualTo("FILE.TXT"));
    Assert.That(entry.FullPath, Is.EqualTo("DIR/FILE.TXT"));
    Assert.That(entry.IsDirectory, Is.False);
    Assert.That(entry.Size, Is.EqualTo(1234));
    Assert.That(entry.StartLba, Is.EqualTo(25));
  }

  [Test, Category("HappyPath")]
  public void EmptyStream_ReturnsNoEntries() {
    using var ms = new MemoryStream(new byte[2352 * 32]);
    var r = new FileFormat.BinCue.BinCueReader(ms);
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension_Bin() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("image.bin");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.BinCue));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension_Cue() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("image.cue");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.BinCue));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.BinCue.BinCueFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTripsThroughReader() {
    var payload = "comic-disc-content"u8.ToArray();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, payload);
      var d = new FileFormat.BinCue.BinCueFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "data.bin", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;

      var r = new FileFormat.BinCue.BinCueReader(ms);
      var fileEntry = r.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.StartsWith("DATA"));
      Assert.That(fileEntry, Is.Not.Null);
      Assert.That(fileEntry!.Size, Is.EqualTo(payload.Length));
      var extracted = r.Extract(fileEntry);
      Assert.That(extracted[..payload.Length], Is.EqualTo(payload));
    } finally {
      File.Delete(tmp);
    }
  }
}
