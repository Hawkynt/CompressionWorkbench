using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.DoubleSpace;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// Locks the "true in-place" contract for the DoubleSpace + DriveSpace CVF
/// add / remove path: bytes outside the touched allocation-table slots,
/// MDFAT entries, BitFAT bits, FAT chain entries, dirent records and
/// freshly-allocated physical runs must remain byte-identical to the
/// source image.
/// </summary>
[TestFixture]
public class DoubleSpaceInPlaceModifyTests {

  private static byte[] BuildImage(CvfVariant variant, params (string Name, byte[] Data)[] files) {
    var w = new DoubleSpaceWriter { Variant = variant };
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  private static int RootDirOffset(byte[] disk) {
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(14));
    if (reservedSectors == 0) reservedSectors = 1;
    var fatCount = disk[16] == 0 ? (byte)2 : disk[16];
    var fatSize = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(22));
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(11));
    if (bytesPerSector == 0) bytesPerSector = 512;
    return (reservedSectors + fatCount * fatSize) * bytesPerSector;
  }

  private static int RootDirBytes(byte[] disk) {
    var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(17));
    return rootEntryCount * 32;
  }

  private static int MdfatByteStart(byte[] disk) {
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(11));
    if (bps == 0) bps = 512;
    return (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(44)) * bps;
  }

  private static int MdfatByteLen(byte[] disk) {
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(11));
    if (bps == 0) bps = 512;
    return (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(48)) * bps;
  }

  private static int BitFatByteStart(byte[] disk) {
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(11));
    if (bps == 0) bps = 512;
    return (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(52)) * bps;
  }

  private static int BitFatByteLen(byte[] disk) {
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(11));
    if (bps == 0) bps = 512;
    return (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(56)) * bps;
  }

  private static int DataByteStart(byte[] disk) {
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(11));
    if (bps == 0) bps = 512;
    return (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(60)) * bps;
  }

  // ========================================================================
  //                              ADD tests
  // ========================================================================

  [Test, Category("InPlace")]
  public void Add_Preserves_PreExistingMdfatEntries() {
    // Pre-existing entries A.TXT + B.TXT — every used MDFAT entry must keep
    // its physical sector / run length / flags after Add() inserts a new file.
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()),
      ("B.TXT", "beta"u8.ToArray()));

    var preMdfat = image.AsSpan(MdfatByteStart(image), MdfatByteLen(image)).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "gamma payload"u8.ToArray());
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "C.TXT", false)]);
    } finally { File.Delete(tmp); }

    var post = ms.ToArray();
    var postMdfat = post.AsSpan(MdfatByteStart(post), MdfatByteLen(post)).ToArray();

    // Pre-existing entries (clusters 2..N for A.TXT, B.TXT) must be untouched.
    // Cluster 2 = A.TXT, cluster 3 = B.TXT (single-cluster files).
    for (var c = 2; c <= 3; c++) {
      var off = c * 4;
      Assert.That(postMdfat.AsSpan(off, 4).ToArray(), Is.EqualTo(preMdfat.AsSpan(off, 4).ToArray()),
        $"MDFAT entry for cluster {c} must be byte-identical after Add()");
    }
  }

  [Test, Category("InPlace")]
  public void Add_Preserves_PreExistingPhysicalRuns() {
    // Pre-existing physical sector bytes (compressed runs) must stay
    // byte-identical after Add() places a new run at the data tail.
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha content"u8.ToArray()));

    var mdfatStart = MdfatByteStart(image);
    var dataStart = DataByteStart(image);

    // Locate A.TXT's physical run via MDFAT cluster 2 entry.
    var entry = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(mdfatStart + 2 * 4));
    var physSector = (int)(entry & 0x1FFFFFu);
    var runSectors = (int)((entry >> 21) & 0x7Fu);
    var physOffset = dataStart + physSector * 512;
    var physLen = runSectors * 512;
    var prePhys = image.AsSpan(physOffset, physLen).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "beta payload here"u8.ToArray());
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "B.TXT", false)]);
    } finally { File.Delete(tmp); }

    var post = ms.ToArray();
    var postPhys = post.AsSpan(physOffset, physLen).ToArray();
    Assert.That(postPhys, Is.EqualTo(prePhys),
      "A.TXT's physical compressed run must be byte-identical after Add()");
  }

  [Test, Category("InPlace")]
  public void Add_Roundtrips_NewEntry() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var beta = "beta payload here for the in-place add path"u8.ToArray();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, beta);
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "B.TXT", false)]);
    } finally { File.Delete(tmp); }

    ms.Position = 0;
    using var r = new DoubleSpaceReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "A.TXT"), Is.True);
    Assert.That(r.Entries.Any(e => e.Name == "B.TXT"), Is.True);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "A.TXT")), Is.EqualTo("alpha"u8.ToArray()));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "B.TXT")), Is.EqualTo(beta));
  }

  [Test, Category("InPlace")]
  public void Add_SetsBitFatBit_ForNewRun() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "gamma content"u8.ToArray());
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "G.TXT", false)]);
    } finally { File.Delete(tmp); }

    var post = ms.ToArray();
    // Every MDFAT entry with flags != 0 must have its BitFAT bit set.
    var mdfatStart = MdfatByteStart(post);
    var mdfatLen = MdfatByteLen(post);
    var bitFatStart = BitFatByteStart(post);
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(post.AsSpan(11));
    for (var i = 0; i < mdfatLen / 4; i++) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(post.AsSpan(mdfatStart + i * 4));
      var flags = (entry >> 28) & 0xFu;
      if (flags == 0) continue;
      var physSector = (int)(entry & 0x1FFFFFu);
      var runSectors = (int)((entry >> 21) & 0x7Fu);
      var firstRegion = physSector * bps / 8192;
      var lastRegion = (physSector * bps + runSectors * bps - 1) / 8192;
      for (var r = firstRegion; r <= lastRegion; r++) {
        var bitPos = bitFatStart + r / 8;
        Assert.That((post[bitPos] & (1 << (r & 7))) != 0, Is.True,
          $"BitFAT region {r} must be marked used for cluster {i}");
      }
    }
  }

  // ========================================================================
  //                            REMOVE tests
  // ========================================================================

  [Test, Category("InPlace")]
  public void Remove_Preserves_OtherFilesMdfatEntries() {
    // Build two files. Remove A. B's MDFAT entry must be byte-identical.
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()),
      ("B.TXT", "beta"u8.ToArray()));

    var preMdfat = image.AsSpan(MdfatByteStart(image), MdfatByteLen(image)).ToArray();
    // Cluster 3 = B.TXT.
    var preBMdfat = preMdfat.AsSpan(3 * 4, 4).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    DoubleSpaceInPlaceModifier.Remove(ms, ["A.TXT"]);
    var post = ms.ToArray();
    var postBMdfat = post.AsSpan(MdfatByteStart(post) + 3 * 4, 4).ToArray();

    Assert.That(postBMdfat, Is.EqualTo(preBMdfat),
      "B.TXT's MDFAT entry must be byte-identical after removing A.TXT");

    // And cluster 2 (A.TXT) should now be zeroed.
    var postAMdfat = post.AsSpan(MdfatByteStart(post) + 2 * 4, 4).ToArray();
    Assert.That(postAMdfat, Is.EqualTo(new byte[4]),
      "A.TXT's MDFAT entry must be zeroed after removal");
  }

  [Test, Category("InPlace")]
  public void Remove_Preserves_OtherFilesPhysicalRuns() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()),
      ("B.TXT", "beta content"u8.ToArray()));

    var mdfatStart = MdfatByteStart(image);
    var dataStart = DataByteStart(image);

    // Locate B.TXT's physical run (cluster 3).
    var entry = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(mdfatStart + 3 * 4));
    var physSector = (int)(entry & 0x1FFFFFu);
    var runSectors = (int)((entry >> 21) & 0x7Fu);
    var physOffset = dataStart + physSector * 512;
    var physLen = runSectors * 512;
    var preBPhys = image.AsSpan(physOffset, physLen).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    DoubleSpaceInPlaceModifier.Remove(ms, ["A.TXT"]);
    var post = ms.ToArray();
    var postBPhys = post.AsSpan(physOffset, physLen).ToArray();

    Assert.That(postBPhys, Is.EqualTo(preBPhys),
      "B.TXT's physical compressed run must be byte-identical after removing A.TXT");
  }

  [Test, Category("InPlace")]
  public void Remove_ScratchesDirent_WithE5() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()),
      ("B.TXT", "beta"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    DoubleSpaceInPlaceModifier.Remove(ms, ["A.TXT"]);
    var post = ms.ToArray();

    // Walk root dir — find the 8.3 entry whose name starts "A " and its
    // first byte must be 0xE5 (scratched marker).
    var rootOffset = RootDirOffset(post);
    var rootBytes = RootDirBytes(post);
    var found = false;
    for (var i = 0; i < rootBytes; i += 32) {
      var off = rootOffset + i;
      var fb = post[off];
      if (fb == 0x00) break;
      if (fb == 0xE5) {
        // Look at the next entry's name to confirm.
        // Skip — we just need at least one E5 to exist.
        found = true;
      }
    }
    Assert.That(found, Is.True, "expected a scratched (0xE5) dirent after Remove");
  }

  [Test, Category("InPlace")]
  public void Remove_Then_List_OmitsRemoved() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()),
      ("B.TXT", "beta payload"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    DoubleSpaceInPlaceModifier.Remove(ms, ["A.TXT"]);

    ms.Position = 0;
    using var r = new DoubleSpaceReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "A.TXT"), Is.False);
    Assert.That(r.Entries.Any(e => e.Name == "B.TXT"), Is.True);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "B.TXT")),
                Is.EqualTo("beta payload"u8.ToArray()));
  }

  [Test, Category("InPlace")]
  public void Remove_Zeroes_FreedPhysicalRun() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "secret payload here"u8.ToArray()));

    var mdfatStart = MdfatByteStart(image);
    var dataStart = DataByteStart(image);

    var entry = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(mdfatStart + 2 * 4));
    var physSector = (int)(entry & 0x1FFFFFu);
    var runSectors = (int)((entry >> 21) & 0x7Fu);
    var physOffset = dataStart + physSector * 512;
    var physLen = runSectors * 512;

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    DoubleSpaceInPlaceModifier.Remove(ms, ["A.TXT"]);
    var post = ms.ToArray();
    var postPhys = post.AsSpan(physOffset, physLen).ToArray();
    Assert.That(postPhys, Is.EqualTo(new byte[physLen]),
      "freed physical run must be zeroed for secure-wipe");
  }

  // ========================================================================
  //                          ROUNDTRIP / MIX tests
  // ========================================================================

  [Test, Category("InPlace")]
  public void AddThenRemove_RoundTrips() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "gamma content for the round-trip"u8.ToArray());
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "G.TXT", false)]);
    } finally { File.Delete(tmp); }

    DoubleSpaceInPlaceModifier.Remove(ms, ["A.TXT"]);

    ms.Position = 0;
    using var r = new DoubleSpaceReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "A.TXT"), Is.False);
    Assert.That(r.Entries.Any(e => e.Name == "G.TXT"), Is.True);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "G.TXT")),
                Is.EqualTo("gamma content for the round-trip"u8.ToArray()));
  }

  [Test, Category("InPlace")]
  public void Replace_FilenameCollision_ReusesSlot() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "first version"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "second version of the file"u8.ToArray());
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "A.TXT", false)]);
    } finally { File.Delete(tmp); }

    ms.Position = 0;
    using var r = new DoubleSpaceReader(ms);
    var live = r.Entries.Where(e => e.Name == "A.TXT").ToList();
    Assert.That(live, Has.Count.EqualTo(1));
    Assert.That(r.Extract(live[0]), Is.EqualTo("second version of the file"u8.ToArray()));
  }

  [Test, Category("InPlace")]
  public void Add_Compressible_EmitsCompressedRun() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("SEED.TXT", "seed"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    // Highly compressible payload — fills several clusters.
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "The quick brown fox jumps over the lazy dog. ", 500)));
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, text);
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "BIG.TXT", false)]);
    } finally { File.Delete(tmp); }

    var post = ms.ToArray();
    // At least one MDFAT entry should have flags=2 (compressed).
    var mdfatStart = MdfatByteStart(post);
    var mdfatLen = MdfatByteLen(post);
    var anyCompressed = false;
    for (var i = 0; i < mdfatLen / 4; i++) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(post.AsSpan(mdfatStart + i * 4));
      if (((entry >> 28) & 0xFu) == 2u) { anyCompressed = true; break; }
    }
    Assert.That(anyCompressed, Is.True,
      "highly compressible payload added in-place should produce at least one compressed run");

    // And round-trip works.
    ms.Position = 0;
    using var r = new DoubleSpaceReader(ms);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "BIG.TXT")), Is.EqualTo(text));
  }

  [Test, Category("InPlace")]
  public void Add_Stored_NoCompression() {
    // Random / incompressible payload — must round-trip as stored. The seed
    // image must be created with enough headroom: the writer budgets the
    // DATA region from the original file list, so we pre-allocate a placeholder
    // file large enough to host the random payload that gets added later.
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("SEED.TXT", new byte[4096]),
      ("PAD.BIN", new byte[8192]));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    // First free the PAD.BIN slot so its physical sectors become available.
    DoubleSpaceInPlaceModifier.Remove(ms, ["PAD.BIN"]);

    var random = new byte[2048];
    new Random(99).NextBytes(random);
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, random);
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "RND.BIN", false)]);
    } finally { File.Delete(tmp); }

    ms.Position = 0;
    using var r = new DoubleSpaceReader(ms);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "RND.BIN")), Is.EqualTo(random));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "SEED.TXT")), Is.EqualTo(new byte[4096]));
  }

  [Test, Category("InPlace")]
  public void DriveSpace_Variant_RoundTrips() {
    var image = BuildImage(CvfVariant.DriveSpace62,
      ("A.TXT", "alpha"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "beta drivespace payload"u8.ToArray());
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "B.TXT", false)]);
    } finally { File.Delete(tmp); }

    ms.Position = 0;
    using var r = new DoubleSpaceReader(ms);
    Assert.That(r.IsDriveSpace, Is.True);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "B.TXT")),
                Is.EqualTo("beta drivespace payload"u8.ToArray()));
  }

  [Test, Category("InPlace")]
  public void VfatLfn_RoundTrips() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "long filename content"u8.ToArray());
      DoubleSpaceInPlaceModifier.Add(ms,
        [new ArchiveInputInfo(tmp, "SomeLongNameWithSpaces.txt", false)]);
    } finally { File.Delete(tmp); }

    ms.Position = 0;
    using var r = new DoubleSpaceReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "SomeLongNameWithSpaces.txt"), Is.True);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "SomeLongNameWithSpaces.txt")),
                Is.EqualTo("long filename content"u8.ToArray()));
  }

  [Test, Category("InPlace")]
  public void Add_Preserves_RootDirentsBeforeInsertSlot() {
    // The dirent for A.TXT must be byte-identical after we add a fresh entry
    // — the new one lands in the first free slot AFTER A.TXT's record.
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()));

    var rootOff = RootDirOffset(image);
    var preFirst32 = image.AsSpan(rootOff, 32).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "beta"u8.ToArray());
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "B.TXT", false)]);
    } finally { File.Delete(tmp); }

    var post = ms.ToArray();
    var postFirst32 = post.AsSpan(rootOff, 32).ToArray();
    Assert.That(postFirst32, Is.EqualTo(preFirst32),
      "first dirent (A.TXT) must be byte-identical after Add(B.TXT)");
  }

  [Test, Category("InPlace")]
  public void DescriptorWiresInPlaceModifier() {
    var image = BuildImage(CvfVariant.DoubleSpace60,
      ("A.TXT", "alpha"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    // Capture A.TXT's MDFAT entry from offset to prove it stays untouched
    // through the descriptor surface (which now delegates to the in-place
    // modifier, not the rebuilder).
    var mdfatStart = MdfatByteStart(image);
    var preA = image.AsSpan(mdfatStart + 2 * 4, 4).ToArray();

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "beta"u8.ToArray());
      var desc = new DoubleSpaceFormatDescriptor();
      ((IArchiveModifiable)desc).Add(ms, [new ArchiveInputInfo(tmp, "B.TXT", false)]);
    } finally { File.Delete(tmp); }

    var post = ms.ToArray();
    var postA = post.AsSpan(MdfatByteStart(post) + 2 * 4, 4).ToArray();
    Assert.That(postA, Is.EqualTo(preA),
      "descriptor surface must route to in-place modifier (A.TXT's MDFAT entry untouched)");
  }
}
