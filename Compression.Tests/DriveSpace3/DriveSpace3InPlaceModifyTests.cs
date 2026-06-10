using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.DoubleSpace;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DriveSpace3;

/// <summary>
/// Locks the "true in-place" contract for the DriveSpace 3 (Win95 Plus! Pack)
/// add / remove path. The Plus! Pack uses the same MDBPB + MDFAT + BitFAT +
/// DATA layout as the DOS DBLSPACE/DRVSPACE family — only the OEM signature
/// (<c>MS_DSP3</c>), CvfSignature (<c>DVR3</c>) and per-cluster codec
/// (MS LZH instead of DS LZ77) differ — so the shared
/// <see cref="DoubleSpaceInPlaceModifier"/> services this variant too. These
/// tests prove that bytes outside the touched allocation-table slots, MDFAT
/// entries, BitFAT bits, FAT chain entries, dirent records and freshly
/// allocated physical runs stay byte-identical.
/// </summary>
[TestFixture]
public class DriveSpace3InPlaceModifyTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new DriveSpace3Writer();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  private static int MdfatByteStart(byte[] disk)
    => (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(44)) * 512;

  private static int MdfatByteLen(byte[] disk)
    => (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(48)) * 512;

  private static int DataByteStart(byte[] disk)
    => (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(60)) * 512;

  // ========================================================================

  [Test, Category("InPlace")]
  public void Add_Preserves_PreExistingMdfatEntries() {
    var image = BuildImage(
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

    // Cluster 2 (A.TXT) + cluster 3 (B.TXT) must be byte-identical.
    for (var c = 2; c <= 3; c++) {
      var off = c * 4;
      Assert.That(postMdfat.AsSpan(off, 4).ToArray(), Is.EqualTo(preMdfat.AsSpan(off, 4).ToArray()),
        $"MDFAT entry for cluster {c} must be byte-identical after Add()");
    }
  }

  [Test, Category("InPlace")]
  public void Add_Preserves_PreExistingPhysicalRuns() {
    var image = BuildImage(
      ("A.TXT", "alpha content data"u8.ToArray()));

    var mdfatStart = MdfatByteStart(image);
    var dataStart = DataByteStart(image);

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
      "A.TXT's physical compressed run must be byte-identical after in-place Add()");
  }

  [Test, Category("InPlace")]
  public void Add_Roundtrips_NewEntry() {
    var image = BuildImage(("A.TXT", "alpha"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "beta in-place payload"u8.ToArray());
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "B.TXT", false)]);
    } finally { File.Delete(tmp); }

    ms.Position = 0;
    using var r = new DriveSpace3Reader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "A.TXT"), Is.True);
    Assert.That(r.Entries.Any(e => e.Name == "B.TXT"), Is.True);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "A.TXT")), Is.EqualTo("alpha"u8.ToArray()));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "B.TXT")),
                Is.EqualTo("beta in-place payload"u8.ToArray()));
  }

  [Test, Category("InPlace")]
  public void Remove_Preserves_OtherFilesMdfatEntries() {
    var image = BuildImage(
      ("A.TXT", "alpha"u8.ToArray()),
      ("B.TXT", "beta"u8.ToArray()));

    var preBMdfat = image.AsSpan(MdfatByteStart(image) + 3 * 4, 4).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    DoubleSpaceInPlaceModifier.Remove(ms, ["A.TXT"]);
    var post = ms.ToArray();

    var postBMdfat = post.AsSpan(MdfatByteStart(post) + 3 * 4, 4).ToArray();
    Assert.That(postBMdfat, Is.EqualTo(preBMdfat),
      "B.TXT's MDFAT entry must be byte-identical after removing A.TXT");

    var postAMdfat = post.AsSpan(MdfatByteStart(post) + 2 * 4, 4).ToArray();
    Assert.That(postAMdfat, Is.EqualTo(new byte[4]),
      "A.TXT's MDFAT entry must be zeroed after removal");
  }

  [Test, Category("InPlace")]
  public void Remove_Then_List_OmitsRemoved() {
    var image = BuildImage(
      ("A.TXT", "alpha"u8.ToArray()),
      ("B.TXT", "beta"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    DoubleSpaceInPlaceModifier.Remove(ms, ["A.TXT"]);

    ms.Position = 0;
    using var r = new DriveSpace3Reader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "A.TXT"), Is.False);
    Assert.That(r.Entries.Any(e => e.Name == "B.TXT"), Is.True);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "B.TXT")), Is.EqualTo("beta"u8.ToArray()));
  }

  [Test, Category("InPlace")]
  public void Remove_Zeroes_FreedPhysicalRun() {
    var image = BuildImage(("A.TXT", "secret data here"u8.ToArray()));

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
      "freed physical run must be zeroed for secure-wipe (DriveSpace 3)");
  }

  [Test, Category("InPlace")]
  public void AddThenRemove_RoundTrips() {
    var image = BuildImage(("A.TXT", "alpha"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "gamma content for round-trip"u8.ToArray());
      DoubleSpaceInPlaceModifier.Add(ms, [new ArchiveInputInfo(tmp, "G.TXT", false)]);
    } finally { File.Delete(tmp); }

    DoubleSpaceInPlaceModifier.Remove(ms, ["A.TXT"]);

    ms.Position = 0;
    using var r = new DriveSpace3Reader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "A.TXT"), Is.False);
    Assert.That(r.Entries.Any(e => e.Name == "G.TXT"), Is.True);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "G.TXT")),
                Is.EqualTo("gamma content for round-trip"u8.ToArray()));
  }

  [Test, Category("InPlace")]
  public void Descriptor_Routes_ToInPlaceModifier() {
    var image = BuildImage(("A.TXT", "alpha"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    // Capture A.TXT's MDFAT entry — must survive the descriptor's Add call
    // (which now delegates to the in-place modifier).
    var preA = image.AsSpan(MdfatByteStart(image) + 2 * 4, 4).ToArray();

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "beta"u8.ToArray());
      var desc = new DriveSpace3FormatDescriptor();
      ((IArchiveModifiable)desc).Add(ms, [new ArchiveInputInfo(tmp, "B.TXT", false)]);
    } finally { File.Delete(tmp); }

    var post = ms.ToArray();
    var postA = post.AsSpan(MdfatByteStart(post) + 2 * 4, 4).ToArray();
    Assert.That(postA, Is.EqualTo(preA),
      "DriveSpace3 descriptor must route to in-place modifier (A.TXT's MDFAT entry untouched)");
  }
}
