using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.TFat;

namespace Compression.Tests.TFat;

[TestFixture]
public class TFatTests {

  // ── Descriptor metadata ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new TFatFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("TFat"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".tfat"));
    Assert.That(d.Extensions, Contains.Item(".tfat"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    // Magic bytes appear at offsets 54 (FAT12/16) or 82 (FAT32) in the BPB.
    var offsets = d.MagicSignatures.Select(s => s.Offset).OrderBy(o => o).ToArray();
    Assert.That(offsets, Is.EqualTo(new[] { 54, 82 }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCreateCapability() {
    var d = new TFatFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    // TFAT in-place modification (the alternating-FAT commit protocol) IS
    // implemented via TFatModifier — the descriptor must now advertise it.
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
  }

  // ── Detection ───────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_StampsTfatFilSysTypeForFat12() {
    var w = new TFatWriter();
    w.AddFile("a.txt", "hello"u8.ToArray());
    var img = w.Build(); // default 2880 sectors → FAT12

    // For FAT12/16 BS_FilSysType lives at offset 54, 8 bytes.
    var tag = Encoding.ASCII.GetString(img, 54, 8);
    Assert.That(tag, Is.EqualTo("TFAT12  "));
    // And nothing in BS_Reserved1 at offset 37: that byte is where FAT records
    // an unclean unmount, so a marker there makes the volume read as damaged.
    Assert.That(img[37], Is.EqualTo(0x00));
    // BPB_NumFATs must be 2.
    Assert.That(img[16], Is.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void TFatReader_AcceptsStampedImage() {
    var w = new TFatWriter();
    w.AddFile("a.txt", "hello"u8.ToArray());
    var img = w.Build();

    using var ms = new MemoryStream(img);
    var r = new TFatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name.ToUpperInvariant(), Is.EqualTo("A.TXT"));
  }

  [Test, Category("ErrorHandling")]
  public void TFatReader_RejectsPlainFatImage() {
    // Build a standard FAT image via the inner FatWriter — no TFAT markers.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("plain.txt", "hi"u8.ToArray());
    var img = w.Build();
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new TFatReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void TFatReader_RejectsTooSmallImage() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new TFatReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void TFatReader_RejectsSingleFatImage() {
    var w = new TFatWriter();
    w.AddFile("a.txt", "x"u8.ToArray());
    var img = w.Build();
    img[16] = 1; // BPB_NumFATs = 1 (illegal for TFAT)
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new TFatReader(ms));
  }

  // ── Active-FAT selection ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void TFatReader_PicksFat2WhenItsSequenceIsHigher() {
    var w = new TFatWriter { InitialSequence = 1 }; // FAT1.seq=1, FAT2.seq=2
    w.AddFile("a.txt", "hello"u8.ToArray());
    var img = w.Build();

    using var ms = new MemoryStream(img);
    var r = new TFatReader(ms);
    Assert.That(r.ActiveFatIndex, Is.EqualTo(1), "FAT2 should win when its sequence is higher");
    Assert.That(r.ActiveSequence, Is.EqualTo(2u));
    Assert.That(r.InactiveSequence, Is.EqualTo(1u));
  }

  [Test, Category("HappyPath")]
  public void TFatReader_PicksFat1WhenItsSequenceIsHigher() {
    // Build image, then swap the sequence numbers so FAT1.seq > FAT2.seq.
    var w = new TFatWriter { InitialSequence = 5 };
    w.AddFile("a.txt", "hello"u8.ToArray());
    var img = w.Build();

    // Locate FAT regions.
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(11));
    var rsv = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(14));
    var fatSize = (int)BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(22));
    var fat1Off = rsv * bps;
    var fat2Off = fat1Off + fatSize * bps;
    var regLen = fatSize * bps;

    // Force FAT1.seq=99, FAT2.seq=0 — FAT1 should win.
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(fat1Off + regLen - 4), 99u);
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(fat2Off + regLen - 4), 0u);

    using var ms = new MemoryStream(img);
    var r = new TFatReader(ms);
    Assert.That(r.ActiveFatIndex, Is.EqualTo(0));
    Assert.That(r.ActiveSequence, Is.EqualTo(99u));
    Assert.That(r.InactiveSequence, Is.EqualTo(0u));
  }

  [Test, Category("HappyPath")]
  public void TFatReader_FallsBackToFat2OnTie() {
    // Both FATs hold seq=7 — convention is FAT2 wins (Microsoft CE default).
    var w = new TFatWriter();
    w.AddFile("a.txt", "hello"u8.ToArray());
    var img = w.Build();

    var bps = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(11));
    var rsv = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(14));
    var fatSize = (int)BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(22));
    var fat1Off = rsv * bps;
    var fat2Off = fat1Off + fatSize * bps;
    var regLen = fatSize * bps;
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(fat1Off + regLen - 4), 7u);
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(fat2Off + regLen - 4), 7u);

    using var ms = new MemoryStream(img);
    var r = new TFatReader(ms);
    Assert.That(r.ActiveFatIndex, Is.EqualTo(1));
  }

  // ── Disagreeing FATs ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void TFatReader_UsesActiveFatChainsWhenFatsDisagree() {
    // Scenario: FAT1 has correct chains, FAT2 has all-zero chain entries.
    // Active-FAT selection by sequence (FAT2.seq > FAT1.seq) would normally
    // pick FAT2, but here we set FAT1.seq higher so FAT1 wins, and FAT2 is
    // corrupted. The reader must read chains from FAT1.
    var w = new TFatWriter();
    var payload = Encoding.UTF8.GetBytes("Transactional data!");
    w.AddFile("greet.txt", payload);
    var img = w.Build();

    var bps = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(11));
    var rsv = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(14));
    var fatSize = (int)BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(22));
    var fat1Off = rsv * bps;
    var fat2Off = fat1Off + fatSize * bps;
    var regLen = fatSize * bps;

    // Corrupt FAT2: zero out all chain entries except the sentinel cluster 0/1.
    // (Leave the first 3 bytes alone — those carry the FAT12 media descriptor +
    // EOC markers for cluster 0/1 and are required for valid FAT structure.)
    for (var i = 3; i < regLen - 4; i++) img[fat2Off + i] = 0;

    // Make FAT1 the committed (active) copy.
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(fat1Off + regLen - 4), 100u);
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(fat2Off + regLen - 4), 50u);

    using var ms = new MemoryStream(img);
    var r = new TFatReader(ms);
    Assert.That(r.ActiveFatIndex, Is.EqualTo(0));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(payload),
      "Reader must use the active FAT's chain entries when FATs disagree.");
  }

  // ── Round-trip ──────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_RoundTrip_SingleFile() {
    var payload = Encoding.UTF8.GetBytes("Hello, TFAT world!");
    var w = new TFatWriter();
    w.AddFile("greet.txt", payload);
    var img = w.Build();

    using var ms = new MemoryStream(img);
    var r = new TFatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Writer_RoundTrip_MultipleFiles() {
    var files = new Dictionary<string, byte[]> {
      ["a.txt"] = Encoding.UTF8.GetBytes("alpha"),
      ["b.txt"] = Encoding.UTF8.GetBytes("beta-beta"),
      ["c.bin"] = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 },
      ["long.dat"] = Encoding.UTF8.GetBytes(new string('x', 4500)),
    };
    var w = new TFatWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    var img = w.Build();

    using var ms = new MemoryStream(img);
    var r = new TFatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(files.Count));
    foreach (var entry in r.Entries) {
      var key = files.Keys.First(k => k.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
      var data = r.Extract(entry);
      Assert.That(data, Is.EqualTo(files[key]), $"Round-trip mismatch for {entry.Name}");
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_And_Extract_RoundTrip() {
    var w = new TFatWriter();
    var payload = Encoding.UTF8.GetBytes("via descriptor");
    w.AddFile("d.txt", payload);
    var img = w.Build();

    var d = new TFatFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name.ToUpperInvariant(), Is.EqualTo("D.TXT"));

    // Extract via descriptor to a temp directory.
    var tempDir = Path.Combine(Path.GetTempPath(), $"tfat-test-{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tempDir);
      ms.Position = 0;
      d.Extract(ms, tempDir, null, null);
      var files = Directory.GetFiles(tempDir);
      Assert.That(files.Length, Is.EqualTo(1));
      var extracted = File.ReadAllBytes(files[0]);
      Assert.That(extracted, Is.EqualTo(payload));
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
    }
  }

  // ── Static IsTfat helper ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void IsTfat_DetectsStampedBpb() {
    var w = new TFatWriter();
    w.AddFile("a.txt", "x"u8.ToArray());
    var img = w.Build();
    Assert.That(TFatReader.IsTfat(img), Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void IsTfat_RejectsPlainFatBpb() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("p.txt", "x"u8.ToArray());
    var img = w.Build();
    Assert.That(TFatReader.IsTfat(img), Is.False);
  }
}
