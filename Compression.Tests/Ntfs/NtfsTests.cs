namespace Compression.Tests.Ntfs;

[TestFixture]
public class NtfsTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello NTFS!"u8.ToArray();
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("TEST.TXT", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Ntfs.NtfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("A.TXT", "First"u8.ToArray());
    w.AddFile("B.TXT", "Second"u8.ToArray());
    w.AddFile("C.BIN", new byte[200]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Ntfs.NtfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("First"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("Second"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(new byte[200]));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_SmallFile_Resident() {
    // Small file should use resident $DATA (< 700 bytes)
    var data = "Small resident data"u8.ToArray();
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("SMALL.TXT", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Ntfs.NtfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Lznt1_RoundTrip() {
    var original = new byte[8192];
    // Fill with compressible data
    for (var i = 0; i < original.Length; i++)
      original[i] = (byte)(i % 26 + 'A');

    var compressed = FileSystem.Ntfs.Lznt1.Compress(original);
    var decompressed = FileSystem.Ntfs.Lznt1.Decompress(compressed, original.Length);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Ntfs.NtfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Ntfs"));
    Assert.That(desc.DisplayName, Is.EqualTo("NTFS"));
    Assert.That(desc.Extensions, Does.Contain(".ntfs"));
    Assert.That(desc.Extensions, Does.Contain(".img"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(3));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileSystem.Ntfs.NtfsFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST.TXT", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpFile);
    }
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Ntfs.NtfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[1024];
    data[0] = 0xEB; data[1] = 0x52; data[2] = 0x90;
    // Leave OEM ID as zeros (not "NTFS    ")
    data[510] = 0x55; data[511] = 0xAA;
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Ntfs.NtfsReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Ntfs.NtfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  // Windows requires $STANDARD_INFORMATION (type 0x10) on every MFT record; chkdsk flags
  // its absence as corruption. Verify it's emitted before $FILE_NAME.
  [Test, Category("RealWorld")]
  public void FileRecord_HasStandardInformationBeforeFileName() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("TEST.TXT", "data"u8.ToArray());
    var disk = w.Build();

    // MFT cluster 2, record size 1024, file starts at record 16.
    var recordOffset = 2 * 4096 + 16 * 1024;
    Assert.That(disk[recordOffset], Is.EqualTo((byte)'F'));

    var firstAttrOffset = System.Buffers.Binary.BinaryPrimitives
      .ReadUInt16LittleEndian(disk.AsSpan(recordOffset + 20));
    var firstAttrType = System.Buffers.Binary.BinaryPrimitives
      .ReadUInt32LittleEndian(disk.AsSpan(recordOffset + firstAttrOffset));
    Assert.That(firstAttrType, Is.EqualTo(0x10u), "First attribute must be $STANDARD_INFORMATION");
  }

  [Test, Category("RealWorld")]
  public void BootSector_HasVolumeSerialNumber() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    var disk = w.Build();
    var serial = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(disk.AsSpan(72));
    Assert.That(serial, Is.Not.Zero, "NTFS volume serial number must be populated.");
  }

  // ── Spec-compliance tests for full $MFT system files ────────────────────

  [Test, Category("RealWorld")]
  public void Writer_AllSystemFilesPopulated() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    var disk = w.Build();
    const int mftOffset = 2 * 4096;
    // Records 0..11 must carry the FILE signature.
    for (var i = 0; i <= 11; i++) {
      var off = mftOffset + i * 1024;
      Assert.That(disk[off + 0], Is.EqualTo((byte)'F'), $"Record {i} missing FILE magic (byte 0)");
      Assert.That(disk[off + 1], Is.EqualTo((byte)'I'), $"Record {i} missing FILE magic (byte 1)");
      Assert.That(disk[off + 2], Is.EqualTo((byte)'L'), $"Record {i} missing FILE magic (byte 2)");
      Assert.That(disk[off + 3], Is.EqualTo((byte)'E'), $"Record {i} missing FILE magic (byte 3)");
    }
    // Records 12..15 are reserved placeholders — they too must carry FILE
    // magic; only their in-use flag is cleared.
    for (var i = 12; i <= 15; i++) {
      var off = mftOffset + i * 1024;
      Assert.That(disk[off + 0], Is.EqualTo((byte)'F'), $"Reserved record {i} missing FILE magic");
    }
  }

  [Test, Category("RealWorld")]
  public void Writer_UpCaseTableIsCorrect() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    var disk = w.Build();
    const int mftOffset = 2 * 4096;
    // Record 10 = $UpCase. Its $DATA attribute is non-resident; real size
    // reported in $DATA header must be exactly 128 KiB (65536 × 2 bytes).
    var rec10 = mftOffset + 10 * 1024;
    // Walk attributes to find $DATA (0x80); real size lives at attr+48.
    var firstAttr = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(rec10 + 20));
    var p = rec10 + firstAttr;
    while (p + 8 < rec10 + 1024) {
      var type = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(p));
      if (type == 0xFFFFFFFF) break;
      var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(p + 4));
      if (type == 0x80) {
        Assert.That(disk[p + 8], Is.EqualTo((byte)1), "$UpCase $DATA must be non-resident");
        var realSize = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(disk.AsSpan(p + 48));
        Assert.That(realSize, Is.EqualTo(65536 * 2), "$UpCase real size must be 128 KiB");
        return;
      }
      p += (int)len;
    }
    Assert.Fail("Record 10 has no $DATA attribute");
  }

  [Test, Category("RealWorld")]
  public void Writer_BitmapReflectsCorrectClusterCount() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("TEST.TXT", new byte[100]);
    var disk = w.Build();

    // Cluster 0 (boot) and cluster 2 (MFT start) must be marked allocated.
    // Cluster 1 is reserved between boot and MFT — per our writer it's marked
    // too (SetRange(bitmap,0,2) covers clusters 0..1).
    const int mftOffset = 2 * 4096;
    var rec6 = mftOffset + 6 * 1024;
    // Find $DATA attribute of record 6 to locate bitmap cluster.
    var firstAttr = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(rec6 + 20));
    var p = rec6 + firstAttr;
    long bitmapStartCluster = -1;
    while (p + 8 < rec6 + 1024) {
      var type = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(p));
      if (type == 0xFFFFFFFF) break;
      var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(p + 4));
      if (type == 0x80) {
        Assert.That(disk[p + 8], Is.EqualTo((byte)1), "$Bitmap $DATA must be non-resident");
        // Data runs header at attr + (ushort at attr+32).
        var runsOff = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(p + 32));
        var runs = p + runsOff;
        var header = disk[runs];
        var lb = header & 0x0F;
        // First cluster (absolute, since previous LCN is 0).
        long cluster = 0;
        for (var i = 0; i < (header >> 4); i++)
          cluster |= (long)disk[runs + 1 + lb + i] << (i * 8);
        bitmapStartCluster = cluster;
        break;
      }
      p += (int)len;
    }
    Assert.That(bitmapStartCluster, Is.GreaterThan(0));

    var bitmapOff = bitmapStartCluster * 4096;
    // Cluster 0 must be set, cluster 2 (MFT) must be set.
    Assert.That((disk[bitmapOff + 0] & 0x01) != 0, Is.True, "cluster 0 must be marked allocated");
    Assert.That((disk[bitmapOff + 0] & 0x04) != 0, Is.True, "cluster 2 (MFT start) must be marked allocated");
  }

  [Test, Category("RealWorld")]
  public void Writer_VolumeInformationHasVersion31() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    var disk = w.Build();
    const int mftOffset = 2 * 4096;
    var rec3 = mftOffset + 3 * 1024;
    // Walk attributes; find $VOLUME_INFORMATION (0x70).
    var firstAttr = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(rec3 + 20));
    var p = rec3 + firstAttr;
    while (p + 8 < rec3 + 1024) {
      var type = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(p));
      if (type == 0xFFFFFFFF) break;
      var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(p + 4));
      if (type == 0x70) {
        var valOff = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(p + 20));
        var val = p + valOff;
        Assert.That(disk[val + 8], Is.EqualTo((byte)3), "$VOLUME_INFORMATION major version must be 3");
        Assert.That(disk[val + 9], Is.EqualTo((byte)1), "$VOLUME_INFORMATION minor version must be 1");
        return;
      }
      p += (int)len;
    }
    Assert.Fail("Record 3 ($Volume) has no $VOLUME_INFORMATION attribute");
  }

  [Test, Category("RealWorld")]
  public void Writer_MftMirrorMatchesFirstFourRecords() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("TEST.TXT", "data"u8.ToArray());
    var disk = w.Build();
    var mftMirrCluster = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(disk.AsSpan(56));
    var mftCluster = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(disk.AsSpan(48));
    // First 4 MFT records must be mirrored byte-for-byte.
    for (var i = 0; i < 4 * 1024; i++) {
      Assert.That(disk[mftMirrCluster * 4096 + i],
        Is.EqualTo(disk[mftCluster * 4096 + i]),
        $"$MFTMirr byte {i} mismatch with MFT");
    }
  }

  [Test, Category("RealWorld")]
  public void Writer_UsaFixupAppliedAtSectorBoundaries() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("A.TXT", "data"u8.ToArray());
    var disk = w.Build();
    const int mftOffset = 2 * 4096;
    // Every populated record must carry USN 0x0001 at offsets 510 and 1022.
    for (var i = 0; i <= 11; i++) {
      var off = mftOffset + i * 1024;
      var s1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(off + 510));
      var s2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(off + 1022));
      Assert.That(s1, Is.EqualTo(0x0001), $"Record {i} sector-1 USN mismatch");
      Assert.That(s2, Is.EqualTo(0x0001), $"Record {i} sector-2 USN mismatch");
    }
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_LargeFile_3Files_VariousSizes() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("small.txt", "Hello"u8.ToArray());
    var mid = new byte[5000];
    for (var i = 0; i < mid.Length; i++) mid[i] = (byte)(i % 251);
    w.AddFile("mid.bin", mid);
    var big = new byte[65000];
    Random.Shared.NextBytes(big);
    w.AddFile("big.bin", big);
    var disk = w.Build(8 * 1024 * 1024);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Ntfs.NtfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    var byName = r.Entries.ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.That(byName["small.txt"], Is.EqualTo("Hello"u8.ToArray()));
    Assert.That(byName["mid.bin"], Is.EqualTo(mid));
    Assert.That(byName["big.bin"], Is.EqualTo(big));
  }
}
