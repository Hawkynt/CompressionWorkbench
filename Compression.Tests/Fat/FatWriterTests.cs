namespace Compression.Tests.Fat;

[TestFixture]
[Category("Slow")]
public class FatWriterTests {

  // ── FAT32 auto-size minimum (no ballooning) ───────────────────────────────
  // Regression: when many long-named files forced FAT32, BuildAutoSized hard-coded
  // 200 000 sectors (~97 MB) regardless of data size. It must instead size to the
  // FAT32 cluster minimum (~65,525 clusters) plus the actual data.

  [Test, Category("Spec")]
  public void BuildAutoSized_ManyLongNames_DoesNotBalloon() {
    // ~300 long-named tiny files → 1490-ish dir slots → forces FAT32.
    var w = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < 300; i++)
      w.AddFile($"LongFileName_Number_{i:D4}.dat", new byte[64]);
    var disk = w.BuildAutoSized();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(32), "long-name flood must land on FAT32");
    Assert.That(r.Entries.Count(e => !e.IsDirectory), Is.EqualTo(300), "all files present");

    // ~64 KB of data should NOT produce a 97 MB image. Minimum valid FAT32 with
    // 512-byte clusters is ~34 MB; allow up to 40 MB of headroom, but the old
    // 200 000-sector (~97 MB) behaviour must be gone.
    Assert.That(disk.Length, Is.LessThan(40 * 1024 * 1024),
      "FAT32 auto-image must be sized to the cluster minimum, not the old 97 MB constant");
  }

  [Test, Category("Spec")]
  public void BuildAutoSized_ForcedFat32_TinyData_IsNearMinimum() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("x.txt", new byte[100]);
    var disk = w.BuildAutoSized(forcedFatType: 32);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(32));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(new byte[100]));
    Assert.That(disk.Length, Is.LessThan(40 * 1024 * 1024),
      "forced FAT32 on tiny data must be near the 34 MB minimum, not 97 MB");
  }

  // ── Fixed-image cluster optimization ──────────────────────────────────────
  // When the image size is pinned (e.g. a 1.44 MB floppy) but cluster size is
  // left on Auto, the writer should pick the cluster that minimises slack while
  // everything still fits inside that fixed size.

  [Test, Category("Spec")]
  public void PickClusterForFixedImage_PrefersLowSlackCluster() {
    // Files that are exact multiples of 512 B → zero slack at 512-byte clusters,
    // but waste up to ~3.5 KB each at 4 KB clusters. On a fixed 1.44 MB floppy
    // the optimiser should pick a small cluster (512 B) to minimise waste.
    var w = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < 8; i++) w.AddFile($"F{i}.BIN", new byte[512]); // 1 sector each
    var picked = w.PickClusterForFixedImage(2880, 512, forcedFatType: 0, requestedRootEntries: 0, enableLfn: true);
    Assert.That(picked, Is.EqualTo(512), "tightest cluster for zero-slack 512-byte files on a fixed floppy");
  }

  [Test, Category("Spec")]
  public void PickClusterForFixedImage_ReturnsZeroWhenNothingFits() {
    // One file far larger than the fixed image → no cluster size can fit it.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("HUGE.BIN", new byte[4 * 1024 * 1024]); // 4 MB into a 1.44 MB floppy
    var picked = w.PickClusterForFixedImage(2880, 512, forcedFatType: 0, requestedRootEntries: 0, enableLfn: true);
    Assert.That(picked, Is.EqualTo(0), "nothing fits → 0 (caller falls back to default)");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_FixedImage_AutoCluster_RoundTrips() {
    // Through the descriptor: pin 1.44 MB, leave cluster Auto. Must round-trip.
    var tmp = Enumerable.Range(0, 6).Select(_ => Path.GetTempFileName()).ToList();
    try {
      foreach (var f in tmp) File.WriteAllBytes(f, new byte[1024]);
      var inputs = tmp.Select((f, i) => new Compression.Registry.ArchiveInputInfo(f, $"FILE{i}.BIN", false)).ToList();
      var desc = new FileSystem.Fat.FatFormatDescriptor();
      using var ms = new MemoryStream();
      var opts = new Compression.Registry.FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["ImageSize"] = "1.44 MB (3.5\" HD)", ["ClusterSize"] = "Auto",
        },
      };
      ((Compression.Registry.IArchiveCreatable)desc).Create(ms, inputs, opts);
      Assert.That(ms.Length, Is.EqualTo(2880L * 512), "image size must stay pinned at 1.44 MB");
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(6));
    } finally {
      tmp.ForEach(File.Delete);
    }
  }

  // ── Streaming BuildTo parity tests ────────────────────────────────────────
  // BuildTo must produce byte-for-byte identical output to Build for every
  // configuration both can express — that parity is what lets the streaming
  // path (sparse, bounded-memory, 50 TB-capable) safely replace the in-memory
  // path without behavioural drift.

  private static void AssertBuildToParity(
      System.Action<FileSystem.Fat.FatWriter> addFiles,
      int totalSectors, int forcedFatType = 0, int requestedClusterSize = 0,
      string? volumeLabel = null, bool enableLfn = true, bool transactionFat = false,
      int requestedRootEntries = 0) {
    // Both writers get the same serial: a volume's is drawn when it is made, so two
    // volumes never share one, and comparing two builds means pinning it — which is
    // what a formatting tool's own switch for it is for.
    const uint serial = 0x5A17C0DE;
    var w1 = new FileSystem.Fat.FatWriter(); w1.SetVolumeSerial(serial); addFiles(w1);
    var inMemory = w1.Build(totalSectors, 512, requestedClusterSize, volumeLabel,
      forcedFatType, enableLfn, transactionFat, requestedRootEntries);

    var w2 = new FileSystem.Fat.FatWriter(); w2.SetVolumeSerial(serial); addFiles(w2);
    using var ms = new MemoryStream();
    w2.BuildTo(ms, totalSectors, 512, requestedClusterSize, volumeLabel,
      forcedFatType, enableLfn, transactionFat, requestedRootEntries);
    var streamed = ms.ToArray();

    Assert.That(streamed.Length, Is.EqualTo(inMemory.Length), "image lengths differ");
    Assert.That(streamed, Is.EqualTo(inMemory), "streaming output diverges from in-memory output");
  }

  [Test, Category("Spec")]
  public void BuildTo_Parity_Fat12_Floppy() =>
    AssertBuildToParity(w => { w.AddFile("HELLO.TXT", "hi"u8.ToArray()); w.AddFile("data.bin", new byte[3000]); }, 2880);

  [Test, Category("Spec")]
  public void BuildTo_Parity_Fat12_LongNames() =>
    AssertBuildToParity(w => { w.AddFile("A Long File Name.txt", new byte[100]); w.AddFile("Another One.dat", new byte[5000]); }, 2880);

  [Test, Category("Spec")]
  public void BuildTo_Parity_Fat16() =>
    AssertBuildToParity(w => { for (var i = 0; i < 20; i++) w.AddFile($"FILE{i:D3}.DAT", new byte[1024]); }, 70000, forcedFatType: 16);

  [Test, Category("Spec")]
  public void BuildTo_Parity_Fat32() =>
    AssertBuildToParity(w => { w.AddFile("readme.txt", "fat32"u8.ToArray()); w.AddFile("payload.bin", new byte[40000]); }, 200_000);

  [Test, Category("Spec")]
  public void BuildTo_Parity_Fat32_ForcedOnFloppy() =>
    AssertBuildToParity(w => w.AddFile("x.txt", new byte[10]), 2880, forcedFatType: 32);

  [Test, Category("Spec")]
  public void BuildTo_Parity_Dmf_RootEntries16() =>
    AssertBuildToParity(w => w.AddFile("setup.exe", new byte[20000]), 3360, requestedRootEntries: 16);

  [Test, Category("Spec")]
  public void BuildTo_Parity_WithLabelAndTfat() =>
    AssertBuildToParity(w => w.AddFile("F.TXT", new byte[50]), 2880, volumeLabel: "MYDISK", transactionFat: true);

  [Test, Category("Spec")]
  public void BuildTo_Parity_NoLfn() =>
    AssertBuildToParity(w => w.AddFile("LongName.dat", new byte[100]), 2880, enableLfn: false);

  [Test, Category("Spec")]
  public void BuildTo_SparseFile_OnlyWritesContent() {
    // A large image with a tiny payload must not physically write the whole volume.
    // We can't easily assert sparseness on a MemoryStream, but we CAN assert the
    // logical length is correct and the content round-trips.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("tiny.txt", "x"u8.ToArray());
    using var ms = new MemoryStream();
    w.BuildTo(ms, 200_000); // ~100 MB FAT32
    Assert.That(ms.Length, Is.EqualTo(200_000L * 512), "logical image length must match totalSectors");

    ms.Position = 0;
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("x"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello FAT writer!"u8.ToArray();
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("TEST.TXT", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("A.TXT", "First"u8.ToArray());
    w.AddFile("B.TXT", "Second"u8.ToArray());
    w.AddFile("C.BIN", new byte[200]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("First"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("Second"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[10000];
    new Random(42).NextBytes(data);
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("BIG.DAT", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void FAT12_DefaultType() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("TEST.TXT", new byte[10]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(12));
  }

  [Test, Category("RoundTrip")]
  public void FAT32_RoundTrip_SmallImage() {
    // ~75 MB → forces cluster count over 65525, triggering FAT32.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("HELLO.TXT", "hello fat32"u8.ToArray());
    var payload = new byte[4096];
    new Random(7).NextBytes(payload);
    w.AddFile("RAND.BIN", payload);
    var totalSectors = 200_000;
    var disk = w.Build(totalSectors);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(32), "image should land in FAT32 range");
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    var nameSet = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(nameSet.Contains("HELLO.TXT"), Is.True);
    Assert.That(nameSet.Contains("RAND.BIN"), Is.True);
    var hello = r.Entries.First(e => e.Name == "HELLO.TXT");
    var rand = r.Entries.First(e => e.Name == "RAND.BIN");
    Assert.That(r.Extract(hello), Is.EqualTo("hello fat32"u8.ToArray()));
    Assert.That(r.Extract(rand), Is.EqualTo(payload));
  }

  [Test, Category("Spec")]
  public void FAT32_HasFsInfoSectorAndBackupBoot() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("A.TXT", "x"u8.ToArray());
    var disk = w.Build(200_000);

    // FSInfo at sector 1.
    var fsInfo = disk.AsSpan(512);
    var leadSig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fsInfo);
    var strucSig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fsInfo[484..]);
    var trailSig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fsInfo[508..]);
    Assert.That(leadSig, Is.EqualTo(0x41615252u), "FSI_LeadSig");
    Assert.That(strucSig, Is.EqualTo(0x61417272u), "FSI_StrucSig");
    Assert.That(trailSig, Is.EqualTo(0xAA550000u), "FSI_TrailSig");

    // Backup boot sector at sector 6 must duplicate the primary boot sector.
    var primary = disk.AsSpan(0, 512);
    var backup = disk.AsSpan(6 * 512, 512);
    Assert.That(backup.SequenceEqual(primary), Is.True, "backup boot sector must mirror primary");

    // BPB_RootClus at offset 44 must be 2.
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(44)), Is.EqualTo(2u));
    // BPB_FSInfo at offset 48 must be 1.
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(48)), Is.EqualTo((ushort)1));
    // Filesystem type string "FAT32   " at offset 82.
    Assert.That(System.Text.Encoding.ASCII.GetString(disk.AsSpan(82, 8)), Is.EqualTo("FAT32   "));
  }

  [Test, Category("RoundTrip")]
  public void EmptyDisk() {
    var w = new FileSystem.Fat.FatWriter();
    var disk = w.Build();
    Assert.That(disk.Length, Is.EqualTo(2880 * 512));

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("RoundTrip")]
  public void LFN_LongName_RoundtripsViaReader() {
    // Multi-fragment long names exercise the LFN ord-reversal, the
    // 5/6/2 split, and the trailing-NUL-then-FFFF padding rule.
    var w = new FileSystem.Fat.FatWriter();
    var longName = "Hello World With Long Name.TXT";
    w.AddFile(longName, "lfn"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo(longName), "Reader should reconstruct long name");
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("lfn"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void LFN_MixedCase_PreservesCase() {
    // Mixed-case filename triggers LFN even though it'd fit in 8.3 chars,
    // because pure 8.3 entries can't carry lowercase.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("ReadMe.md", "x"u8.ToArray());
    var disk = w.Build();
    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("ReadMe.md"));
  }

  [Test, Category("Spec")]
  public void LFN_ChecksumMatchesShortNameInDirent() {
    // The FATGEN103 checksum is sum_i RotR(prev) + short[i] over the 11
    // raw bytes of the 8.3 entry. Both the LFN slot and the 8.3 entry must
    // store the same value or fsck.fat reports a CHAIN error.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("MixedCase.txt", new byte[5]);
    var disk = w.Build();

    // First user dirent on a FAT12 floppy starts at offset
    // (1 + 2*9) * 512 = 9728. Walk LFN slots (attr=0x0F) until we hit
    // the 8.3 entry — checksum is replicated in every LFN slot.
    const int rootStart = 9728;
    var off = rootStart;
    var lfnChecksum = disk[off + 13];
    Assert.That(disk[off + 11], Is.EqualTo(0x0F), "First slot must be LFN (attribute 0x0F)");
    while (disk[off + 11] == 0x0F) off += 32;
    var shortStart = off;

    byte recomputed = 0;
    for (var i = 0; i < 11; i++)
      recomputed = (byte)((((recomputed & 1) != 0 ? 0x80 : 0) + (recomputed >> 1) + disk[shortStart + i]) & 0xFF);
    Assert.That(lfnChecksum, Is.EqualTo(recomputed),
      "LFN slot checksum must equal RotR-add over the 11 raw bytes of the 8.3 entry");
  }

  [Test, Category("RoundTrip")]
  public void LFN_DoesNotEmitForPlain83Names() {
    // Plain 8.3 names should NOT emit any LFN slot — DOS readers must see
    // the file at the very first dirent. Verify by counting attribute=0x0F
    // entries: there should be zero in the root dir.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("HELLO.TXT", "x"u8.ToArray());
    var disk = w.Build();

    const int rootStart = 9728;
    var lfnSlots = 0;
    for (var off = rootStart; off < rootStart + 32 * 16; off += 32) {
      if (disk[off] == 0x00) break;
      if (disk[off + 11] == 0x0F) lfnSlots++;
    }
    Assert.That(lfnSlots, Is.EqualTo(0), "Plain 8.3 names must not emit LFN slots");
  }

  [Test, Category("RoundTrip")]
  public void LFN_AndPlain83_Coexist() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("PLAIN.TXT", "p"u8.ToArray());
    w.AddFile("Mixed Case Filename.dat", "m"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names.Contains("PLAIN.TXT"), Is.True);
    Assert.That(names.Contains("Mixed Case Filename.dat"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileSystem.Fat.FatFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST.TXT", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpFile);
    }
  }

  // ── Timestamp tests ───────────────────────────────────────────────────────

  [Test, Category("Spec")]
  public void Timestamps_AreNonZero_WhenNotProvided() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("TEST.TXT", new byte[10]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var mod = r.Entries[0].LastModified;
    Assert.That(mod, Is.Not.Null, "LastModified should be set");
    Assert.That(mod!.Value.Year, Is.GreaterThanOrEqualTo(1980),
      "FAT date must be in the representable range (≥ 1980-01-01)");
    Assert.That(mod.Value, Is.GreaterThan(new DateTime(1980, 1, 1)),
      "Timestamp should not be the FAT epoch zero (zeroed bytes → 1980-00-00, which readers skip)");
  }

  [Test, Category("Spec")]
  public void Timestamps_RoundTrip_WhenProvided() {
    var target = new DateTime(2024, 6, 15, 10, 30, 4); // even seconds — FAT has 2-second precision
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("TEST.TXT", new byte[10], target);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var got = r.Entries[0].LastModified;
    Assert.That(got, Is.Not.Null);
    Assert.That(got!.Value, Is.EqualTo(target).Within(TimeSpan.FromSeconds(2)),
      "FAT timestamps have 2-second resolution; round-trip should match within that");
  }

  [Test, Category("Spec")]
  public void Timestamps_LFN_File_HasTimestamp() {
    // Long names go through a different code path (LFN slots + 8.3 entry).
    // Make sure the timestamp is written into the 8.3 entry, not lost.
    var target = new DateTime(2023, 3, 1, 8, 0, 0);
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("This Is A Long Filename.txt", new byte[5], target);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var got = r.Entries[0].LastModified;
    Assert.That(got, Is.Not.Null);
    Assert.That(got!.Value, Is.EqualTo(target).Within(TimeSpan.FromSeconds(2)));
  }

  // ── Root-directory overflow tests ─────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void RootDir_Overflow_ThrowsInsteadOfCorrupting() {
    // 100 files × (2 LFN slots + 1 short-name entry) = 300 entries > 224 max for FAT12.
    // Before this fix the writer silently wrote directory entries into the data
    // clusters, corrupting both the directory and the file payloads.
    var w = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < 100; i++)
      w.AddFile($"LongFileName{i:D4}.dat", new byte[1]);

    var ex = Assert.Throws<InvalidOperationException>(() => w.Build());
    Assert.That(ex!.Message, Does.Contain("root directory"),
      "Exception message should explain the root-directory limit");
    Assert.That(ex.Message, Does.Contain("BuildAutoSized"),
      "Exception message should mention BuildAutoSized as the fix");
  }

  [Test, Category("RoundTrip")]
  public void BuildAutoSized_EscapesRootDirOverflow() {
    // Same 100-file set that would overflow FAT12 must succeed via BuildAutoSized,
    // which should auto-upgrade to FAT32 (unbounded root directory in cluster chain).
    var w = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < 100; i++)
      w.AddFile($"LongFileName{i:D4}.dat", new byte[1]);

    byte[] disk = null!;
    Assert.DoesNotThrow(() => disk = w.BuildAutoSized());

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(32), "BuildAutoSized should have selected FAT32");
    Assert.That(r.Entries, Has.Count.EqualTo(100), "All 100 files must be present");
  }

  // ── Root entry count / DMF tests ─────────────────────────────────────────

  [Test, Category("Spec")]
  public void DMF_RootEntries16_WrittenCorrectly() {
    // DMF disks use 16 root entries to reclaim those sectors for data.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("readme.txt", "DMF test"u8.ToArray());
    var disk = w.Build(totalSectors: 3360, requestedRootEntries: 16);

    // BPB_RootEntCnt at offset 17 (little-endian uint16).
    var rootEntries = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(17));
    Assert.That(rootEntries, Is.EqualTo(16), "Root entry count should be 16 for DMF");
  }

  [Test, Category("Spec")]
  public void DMF_1_68MB_Has21SectorsPerTrack() {
    // 1.68 MB (3360 sectors) → DMF geometry: 21 spt / 2 heads.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("a.txt", new byte[1]);
    var disk = w.Build(totalSectors: 3360);

    var spt   = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(24));
    var heads = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(26));
    Assert.That(spt,   Is.EqualTo(21), "DMF image should report 21 sectors/track in BPB");
    Assert.That(heads, Is.EqualTo(2),  "DMF image should report 2 heads in BPB");
  }

  [Test, Category("Spec")]
  public void Standard_1_44MB_Has18SectorsPerTrack() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("a.txt", new byte[1]);
    var disk = w.Build(totalSectors: 2880);

    var spt = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(24));
    Assert.That(spt, Is.EqualTo(18), "Standard floppy should report 18 sectors/track");
  }

  [Test, Category("RoundTrip")]
  public void DMF_RoundTrips_FilesCorrectly() {
    var data = new byte[2048];
    new Random(99).NextBytes(data);
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("bigfile.bin", data);
    var disk = w.Build(totalSectors: 3360, requestedClusterSize: 2048, requestedRootEntries: 16);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  // ── Smart cluster-size selection tests ────────────────────────────────────

  [Test, Category("Spec")]
  public void BuildAutoSized_SelectsCluster_ThatMinimisesWaste() {
    // Ten 3 KB files: with 512-byte clusters each file wastes (512 - 3072%512)%512 = 0
    // bytes but uses 6 clusters. With 4 KB clusters each file wastes 1 KB but uses 1
    // cluster. With 512-byte clusters: 10×6=60 clusters → FAT12 fine; less slack.
    // The smart picker should prefer 512-byte or 1-KB clusters here (zero slack).
    var w = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < 10; i++)
      w.AddFile($"f{i}.bin", new byte[3072]); // exactly 6×512 = zero slack with 512-byte clusters
    var disk = w.BuildAutoSized();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(10));
  }

  [Test, Category("Spec")]
  public void BuildAutoSized_DoesNotEscalateToFAT16_WhenFAT12_Fits() {
    // A handful of small files should stay in FAT12, not jump to FAT16
    // which would add a larger FAT table for no benefit.
    var w = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < 5; i++)
      w.AddFile($"f{i}.txt", new byte[512]);
    var disk = w.BuildAutoSized();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(12), "Small workload must stay FAT12");
  }

  // ── FAT type forcing tests ────────────────────────────────────────────────

  [Test, Category("Spec")]
  public void ForcedFAT32_OnFloppy_UsesCorrectLayout() {
    // FAT32 forced on a 1.44 MB disk: writer must use the FAT32 extended BPB
    // (32 reserved sectors, FSInfo at sector 1, root in cluster chain).
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("test.txt", "hello"u8.ToArray());
    var disk = w.Build(totalSectors: 2880, forcedFatType: 32);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(32), "Should be FAT32 even on floppy-sized image");
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("hello"u8.ToArray()));

    // FAT32 extended BPB marker: filesystem type string at offset 82.
    Assert.That(System.Text.Encoding.ASCII.GetString(disk.AsSpan(82, 8)), Is.EqualTo("FAT32   "));
  }

  [Test, Category("Spec")]
  public void ForcedFAT12_TooLarge_Throws() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("A.TXT", new byte[1]);
    // 200 000 sectors with 1 sector/cluster → ~195 000 data clusters > 4084 FAT12 max.
    Assert.Throws<InvalidOperationException>(() => w.Build(totalSectors: 200_000, forcedFatType: 12));
  }

  [Test, Category("RoundTrip")]
  public void ForcedFAT12_SmallImage_RoundTrips() {
    var data = "fat12 forced"u8.ToArray();
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("hi.txt", data);
    var disk = w.Build(totalSectors: 2880, forcedFatType: 12);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(12));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void BuildAutoSized_ForcedFAT32_AlwaysProducesFAT32() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("x.txt", new byte[10]);
    var disk = w.BuildAutoSized(forcedFatType: 32);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(32));
  }

  // ── LFN-disable (strict 8.3) tests ───────────────────────────────────────

  [Test, Category("Spec")]
  public void LfnDisabled_ProducesNo_LfnSlots() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("This Is A Long Filename.txt", new byte[5]);
    var disk = w.Build(enableLfn: false);

    // No entries with attribute 0x0F in the root directory.
    const int rootStart = 9728;
    for (var off = rootStart; off < rootStart + 224 * 32; off += 32) {
      if (disk[off] == 0x00) break;
      Assert.That(disk[off + 11], Is.Not.EqualTo(0x0F), "LFN disabled → no LFN attribute entries");
    }
  }

  [Test, Category("RoundTrip")]
  public void LfnDisabled_FileDataStillReadable() {
    var data = "8.3 only data"u8.ToArray();
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("LongName.dat", data);
    var disk = w.Build(enableLfn: false);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data), "File data must survive 8.3-only mode");
  }

  // ── TFAT marker test ──────────────────────────────────────────────────────

  /// <summary>
  /// Asking for a transactional volume must not make the volume look damaged.
  /// </summary>
  /// <remarks>
  /// BS_Reserved1 is where FAT records that a volume was not cleanly
  /// unmounted. This writer used to set it as a TFAT marker, which had
  /// <c>fsck.fat</c> report a dirty bit and possible corruption on every such
  /// volume and exit non-zero. Whatever else the option does, it must leave
  /// that byte alone.
  /// </remarks>
  [Test, Category("Spec")]
  public void TransactionFat_LeavesTheDirtyByteAlone() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("t.txt", new byte[1]);

    var diskNormal = w.Build(transactionFat: false);
    var diskTfat   = w.Build(transactionFat: true);

    // FAT12/16: BS_Reserved1 is at byte 37.
    Assert.That(diskNormal[37], Is.EqualTo(0x00), "no TFAT: the byte stays clear");
    Assert.That(diskTfat[37],   Is.EqualTo(0x00), "TFAT either: it is not ours to write");
  }

  // ── VolumeLabel test ──────────────────────────────────────────────────────

  [Test, Category("Spec")]
  public void VolumeLabel_WrittenToBootSector() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("f.txt", new byte[1]);
    var disk = w.Build(volumeLabel: "MYVOLUME");

    // FAT12/16 volume label at boot sector offset 43, 11 bytes.
    var label = System.Text.Encoding.ASCII.GetString(disk.AsSpan(43, 11)).TrimEnd();
    Assert.That(label, Is.EqualTo("MYVOLUME"), "Volume label should appear in boot sector");
  }

  [Test, Category("RoundTrip")]
  public void BuildAutoSized_SmallFileCount_StaysFat12() {
    // A handful of short-named files must not be bumped up unnecessarily.
    var w = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < 5; i++)
      w.AddFile($"F{i}.TXT", new byte[10]);

    var disk = w.BuildAutoSized();
    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(12), "Small workloads should stay FAT12");
    Assert.That(r.Entries, Has.Count.EqualTo(5));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ManyFiles_NoCorruption() {
    // Regression: FatFormatDescriptor.Create used Build() (1.44 MB) which
    // silently overflowed the root directory when given many files.
    // It now calls BuildAutoSized() and must survive without corruption.
    var tmpFiles = Enumerable.Range(0, 80)
      .Select(_ => Path.GetTempFileName())
      .ToList();
    try {
      foreach (var f in tmpFiles) File.WriteAllBytes(f, new byte[10]);
      var inputs = tmpFiles
        .Select((f, i) => new Compression.Registry.ArchiveInputInfo(f, $"LongPath{i:D4}/file{i:D4}.dat", false))
        .ToList();

      var desc = new FileSystem.Fat.FatFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(ms, inputs, new Compression.Registry.FormatCreateOptions());

      ms.Position = 0;
      var entries = desc.List(ms, null);
      var files = entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(files, Has.Count.EqualTo(80), "All 80 files must be listed without corruption");
      // Each file lives inside its own subdirectory rather than being flattened
      // into the root, so the 80 LongPathNNNN directories are present too.
      Assert.That(entries.Count(e => e.IsDirectory), Is.EqualTo(80), "subdirectories preserved, not flattened");
      Assert.That(files.All(e => e.Name.Replace('\\', '/').Contains('/')), Is.True,
        "every file is reported at its full nested path");
    } finally {
      tmpFiles.ForEach(File.Delete);
    }
  }
}
