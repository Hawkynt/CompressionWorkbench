using System.Text;
using Compression.Registry;
using FileSystem.Adfs;

namespace Compression.Tests.Adfs;

/// <summary>
/// WORM (write-then-read) round-trip tests for the ADFS-L writer. Covers happy
/// path (write+read+verify), boundary cases (empty disc, single byte, sector
/// alignment, max-entry-count), and the descriptor-level
/// <see cref="IArchiveCreatable.Create"/> surface.
/// </summary>
[TestFixture]
public class AdfsWormTests {

  // ── HappyPath: writer round-trips through reader ───────────────────────

  [Test, Category("HappyPath")]
  public void Writer_SingleFile_RoundTripsThroughReader() {
    var content = "Hello from ADFS WORM!\n"u8.ToArray();
    var w = new AdfsWriter();
    w.AddFile("HELLO", content);
    var img = w.Build();

    using var ms = new MemoryStream(img);
    using var r = new AdfsReader(ms);
    Assert.That(r.DirectoryMagic, Is.EqualTo("Hugo"));
    Assert.That(r.SectorSize, Is.EqualTo(256));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Writer_MultipleFiles_RoundTripsThroughReader() {
    var a = "ALPHA content"u8.ToArray();
    var b = new byte[600]; for (var i = 0; i < b.Length; i++) b[i] = (byte)(i & 0xFF);
    var c = new byte[1500]; for (var i = 0; i < c.Length; i++) c[i] = (byte)((i * 7) & 0xFF);

    var w = new AdfsWriter();
    w.AddFile("ALPHA", a);
    w.AddFile("BRAVO", b);
    w.AddFile("CHARLIE", c);  // SanitizeName truncates: 7 chars fits in 9-byte slot.
    var img = w.Build();

    using var r = new AdfsReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("ALPHA"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("BRAVO"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("CHARLIE"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(a));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(b));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(c));
  }

  // ── Boundary cases ─────────────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void Writer_EmptyDisc_ProducesValidImage() {
    var w = new AdfsWriter();
    var img = w.Build();
    Assert.That(img.Length, Is.EqualTo(AdfsWriter.DiskSizeL));
    using var r = new AdfsReader(new MemoryStream(img));
    Assert.That(r.Entries, Is.Empty);
    Assert.That(r.DirectoryMagic, Is.EqualTo("Hugo"));
  }

  [Test, Category("Boundary")]
  public void Writer_OneByteFile_RoundTripsExact() {
    var w = new AdfsWriter();
    w.AddFile("X", [0x42]);
    var img = w.Build();
    using var r = new AdfsReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(new byte[] { 0x42 }));
  }

  [Test, Category("Boundary")]
  public void Writer_SectorAlignedFile_RoundTripsExact() {
    // 256-byte file exactly fills one sector, no tail slack.
    var data = new byte[256];
    new Random(0xACE).NextBytes(data);
    var w = new AdfsWriter();
    w.AddFile("FULL", data);
    var img = w.Build();
    using var r = new AdfsReader(new MemoryStream(img));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("Boundary")]
  public void Writer_ZeroByteFile_RoundTrips() {
    var w = new AdfsWriter();
    w.AddFile("EMPTY", []);
    var img = w.Build();
    using var r = new AdfsReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("Boundary")]
  public void Writer_MaxEntries_47Files_RoundTrips() {
    var w = new AdfsWriter();
    for (var i = 0; i < 47; i++)
      w.AddFile($"F{i:D2}", Encoding.ASCII.GetBytes($"data-{i}"));
    var img = w.Build();
    using var r = new AdfsReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(47));
    for (var i = 0; i < 47; i++) {
      Assert.That(r.Entries[i].Name, Is.EqualTo($"F{i:D2}"));
      Assert.That(Encoding.ASCII.GetString(r.Extract(r.Entries[i])), Is.EqualTo($"data-{i}"));
    }
  }

  // ── Exception / overflow ───────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Writer_TooManyEntries_Throws() {
    var w = new AdfsWriter();
    for (var i = 0; i < 48; i++)
      w.AddFile($"X{i:D2}", [(byte)i]);
    Assert.Throws<InvalidOperationException>(() => w.Build());
  }

  [Test, Category("ErrorHandling")]
  public void Writer_OverflowingDisc_Throws() {
    // Try to write a single file larger than the disc.
    var w = new AdfsWriter();
    w.AddFile("HUGE", new byte[AdfsWriter.DiskSizeL + 1]);
    Assert.Throws<InvalidOperationException>(() => w.Build());
  }

  // ── Descriptor-level IArchiveCreatable ─────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface_RoundTrips() {
    var tmp = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "DESCRIPTOR-TEST"u8.ToArray());
      var payload = new byte[1000]; for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
      File.WriteAllBytes(tmp2, payload);

      var desc = new AdfsFormatDescriptor();
      using var ms = new MemoryStream();
      ((IArchiveCreatable)desc).Create(ms,
        [new ArchiveInputInfo(tmp, "GREETING", false), new ArchiveInputInfo(tmp2, "BIG", false)],
        new FormatCreateOptions());

      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(2));
      Assert.That(entries.Select(e => e.Name), Does.Contain("GREETING"));
      Assert.That(entries.Select(e => e.Name), Does.Contain("BIG"));

      ms.Position = 0;
      var got1 = desc.ExtractEntryToMemory(ms, "GREETING", null);
      Assert.That(Encoding.ASCII.GetString(got1), Is.EqualTo("DESCRIPTOR-TEST"));

      ms.Position = 0;
      var got2 = desc.ExtractEntryToMemory(ms, "BIG", null);
      Assert.That(got2, Is.EqualTo(payload));
    } finally {
      File.Delete(tmp);
      File.Delete(tmp2);
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_WithVolumeLabel_PersistsToImage() {
    var desc = new AdfsFormatDescriptor();
    using var ms = new MemoryStream();
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYDISC" },
    };
    ((IArchiveCreatable)desc).Create(ms,
      [ArchiveInputInfo.InMemory("FILE", "x"u8.ToArray())],
      opts);

    // The DiscTitle is stored at root-dir offset 0x4DC (19 bytes ASCII). Verify it.
    var img = ms.ToArray();
    var titleSpan = img.AsSpan(0x200 + 0x4DC, 6);
    Assert.That(Encoding.ASCII.GetString(titleSpan), Is.EqualTo("MYDISC"));
  }

  // ── Image structure invariants (FSM check bytes, end pointer parity) ────

  [Test, Category("Spec")]
  public void Writer_OldMapEndPointers_MatchBetweenSectors() {
    var w = new AdfsWriter();
    w.AddFile("F", "data"u8.ToArray());
    var img = w.Build();
    // FreeEnd0 (sector 0 byte 0xFE) must equal FreeEnd1 (sector 1 byte 0xFE).
    Assert.That(img[0x0FE], Is.EqualTo(img[0x1FE]),
      "Old-map FSM end pointers must match between sector 0 and sector 1.");
  }

  [Test, Category("Spec")]
  public void Writer_OldMapDiscId_MatchesBetweenSectors() {
    var w = new AdfsWriter { DiscId = 0xABCD };
    w.AddFile("F", "data"u8.ToArray());
    var img = w.Build();
    Assert.That(img[0x0FB], Is.EqualTo(img[0x1FB]), "DiscId low byte must match between sectors 0 and 1.");
    Assert.That(img[0x0FC], Is.EqualTo(img[0x1FC]), "DiscId high byte must match between sectors 0 and 1.");
    Assert.That((img[0x0FB] | (img[0x0FC] << 8)), Is.EqualTo(0xABCD));
  }

  [Test, Category("Spec")]
  public void Writer_FreeSpaceMap_PointsAtCorrectFreeRegion() {
    var fileLen = 300;  // Spans 2 sectors.
    var w = new AdfsWriter();
    w.AddFile("F", new byte[fileLen]);
    var img = w.Build();

    // First data sector is 7 (after FSM[0,1] + root dir[2..6]); file spans
    // sectors 7,8 (300 bytes = 2 sectors), so free space starts at sector 9.
    var freeStart = img[0] | (img[1] << 8) | (img[2] << 16);
    Assert.That(freeStart, Is.EqualTo(9), "Free fragment should start right after the file's data sectors.");

    var totalSectors = AdfsWriter.DiskSizeL / AdfsWriter.SectorSize;
    var freeLen = img[256] | (img[257] << 8) | (img[258] << 16);
    Assert.That(freeLen, Is.EqualTo(totalSectors - 9),
      "Free fragment length should be totalSectors - firstFreeSector.");
  }
}
