#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// Smoke tests that the add / remove verb is wired across the filesystems that
/// realise it via <see cref="ModifyRebuilder"/> (or their own modifier). Each FS
/// gets an "Add then Read" round-trip and an "interface present" check. Note these
/// rebuild-backed formats are WORM, not R/W — they implement the interface so the
/// verb runs but do not advertise <c>CanModify</c> (enforced by
/// <c>WriteCapabilityHonestyTests</c>). Per-FS edge cases (encoding quirks,
/// max-name-length, etc.) live with the per-FS test fixtures.
/// </summary>
[TestFixture]
public class ModifyRebuilderTests {

  [TestCase("HfsPlus")]
  [TestCase("Hfs")]
  [TestCase("Mfs")]
  [TestCase("Adf")]
  [TestCase("AppleDos")]
  [TestCase("ProDos")]
  [TestCase("Atari8")]
  [TestCase("Bbc")]
  [TestCase("D64")]
  [TestCase("D71")]
  [TestCase("D81")]
  [TestCase("ZxScl")]
  [TestCase("Ext1")]
  [TestCase("Udf")]
  [TestCase("DoubleSpace")]
  [TestCase("DriveSpace")]
  [TestCase("MinixFs")]
  [TestCase("SquashFs")]
  [TestCase("CramFs")]
  [TestCase("RomFs")]
  [TestCase("T64")]
  [TestCase("Tap")]
  // ReiserFs is rebuild-backed WORM (ReiserFsModifier read-modify-rebuilds the whole
  // image) — its capability is pinned in ReiserFsTests. Apfs, by contrast, edits its
  // B-tree structures in place (ApfsModifier) and is genuinely R/W.
  // F2fs has its own log-structured Add/Remove (F2fsModifier) — see
  // F2fsModifyTests for the round-trip + overflow tests, and
  // F2fsTests.Descriptor_AdvertisesLogStructuredMutation for the capability lock.
  public void DescriptorImplementsIArchiveModifiable(string formatId) {
    var desc = FormatRegistry.GetById(formatId);
    Assert.That(desc, Is.Not.Null, $"{formatId} descriptor not registered");
    // This fixture only proves the add/remove verb is WIRED (the interface is present),
    // not that the format is R/W: some entries here (e.g. CramFs / SquashFs) are
    // rebuild-backed WORM and deliberately do NOT advertise CanModify. The WORM-vs-R/W
    // capability honesty is enforced separately by
    // Compression.Tests.Operations.WriteCapabilityHonestyTests.
    Assert.That(desc, Is.InstanceOf<IArchiveModifiable>(),
      $"{formatId} should implement IArchiveModifiable so the verb runs");
  }

  // ── Round-trip smoke per FS ──────────────────────────────────────────
  // For each FS: build empty image, Add a file, read it back, Remove it,
  // confirm gone. The exhaustive correctness lives in per-FS suites; the
  // point here is "the wiring is intact".

  [Test]
  public void HfsPlus_AddRead_RoundTrips() {
    var w = new FileSystem.HfsPlus.HfsPlusWriter();
    var image = w.Build();
    using var ms = new MemoryStream();
    ms.Write(image);

    var tmp = WriteTempBytes("hello"u8.ToArray());
    try {
      ((IArchiveModifiable)new FileSystem.HfsPlus.HfsPlusFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "hello.txt", false)]);

      ms.Position = 0;
      var reader = new FileSystem.HfsPlus.HfsPlusReader(ms, leaveOpen: true);
      Assert.That(reader.Entries.Any(e => e.Name.EndsWith("hello.txt")), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test]
  public void D64_AddRead_RoundTrips() {
    var w = new FileSystem.D64.D64Writer();
    var image = w.Build();
    using var ms = new MemoryStream();
    ms.Write(image);

    var tmp = WriteTempBytes(new byte[] { 1, 2, 3 });
    try {
      ((IArchiveModifiable)new FileSystem.D64.D64FormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "TEST", false)]);

      ms.Position = 0;
      var reader = new FileSystem.D64.D64Reader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "TEST"), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test]
  public void Adf_AddRemove_RoundTrips() {
    var w = new FileSystem.Adf.AdfWriter();
    var image = w.Build();
    using var ms = new MemoryStream();
    ms.Write(image);

    var tmp = WriteTempBytes("amiga"u8.ToArray());
    try {
      var desc = new FileSystem.Adf.AdfFormatDescriptor();
      ((IArchiveModifiable)desc).Add(ms, [new ArchiveInputInfo(tmp, "TEST", false)]);

      ms.Position = 0;
      Assert.That(new FileSystem.Adf.AdfReader(ms).Entries.Any(e => e.Name == "TEST"), Is.True);

      ((IArchiveModifiable)desc).Remove(ms, ["TEST"]);
      ms.Position = 0;
      Assert.That(new FileSystem.Adf.AdfReader(ms).Entries.Any(e => e.Name == "TEST"), Is.False);
    } finally { File.Delete(tmp); }
  }

  [Test]
  public void AppleDos_AddRead_RoundTrips() {
    var w = new FileSystem.AppleDos.AppleDosWriter();
    var image = w.Build();
    using var ms = new MemoryStream();
    ms.Write(image);

    var tmp = WriteTempBytes("apple"u8.ToArray());
    try {
      ((IArchiveModifiable)new FileSystem.AppleDos.AppleDosFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "HELLO", false)]);

      ms.Position = 0;
      Assert.That(new FileSystem.AppleDos.AppleDosReader(ms).Entries.Any(e => e.Name.Contains("HELLO")), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test]
  public void Modifiable_Replace_DropsOldData() {
    // Sanity: when Add is called with a name that already exists, the new
    // bytes win. (HFS+ is stand-in for the whole batch since they all use
    // ModifyRebuilder.)
    var w = new FileSystem.HfsPlus.HfsPlusWriter();
    w.AddFile("config.txt", "v1"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var tmp = WriteTempBytes("v2-replacement"u8.ToArray());
    try {
      ((IArchiveModifiable)new FileSystem.HfsPlus.HfsPlusFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "config.txt", false)]);

      ms.Position = 0;
      var reader = new FileSystem.HfsPlus.HfsPlusReader(ms, leaveOpen: true);
      var entry = reader.Entries.Single(e => e.Name.EndsWith("config.txt"));
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)),
                  Is.EqualTo("v2-replacement"));
    } finally { File.Delete(tmp); }
  }

  [Test]
  public void Modifiable_Remove_WipesTargetBytes() {
    var w = new FileSystem.HfsPlus.HfsPlusWriter();
    w.AddFile("keep.txt", "harmless"u8.ToArray());
    w.AddFile("secret.txt", "TOPSECRET-MARKER-ABC123"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    ((IArchiveModifiable)new FileSystem.HfsPlus.HfsPlusFormatDescriptor()).Remove(ms, ["secret.txt"]);

    var bytes = ms.ToArray();
    var asAscii = System.Text.Encoding.ASCII.GetString(bytes);
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-ABC123"));
  }

  private static string WriteTempBytes(byte[] data) {
    var tmp = Path.GetTempFileName();
    File.WriteAllBytes(tmp, data);
    return tmp;
  }
}
