using System.Buffers.Binary;
using System.Text;
using FileSystem.CpcDsk;

namespace Compression.Tests.CpcDsk;

[TestFixture]
public class CpcDskTests {

  // ── DSK image builders ─────────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal Standard CPC DSK image with the given geometry.
  /// Each sector is filled with the provided sectorFill data (or zeros).
  /// </summary>
  private static byte[] BuildStandardDsk(int tracks, int sides, int sectorsPerTrack,
      int sectorSize, byte[][]? sectorFills = null) {
    var sizeCode = SizeCode(sectorSize);
    var trackBlockSize = 256 + sectorsPerTrack * sectorSize;
    var totalSize = 256 + tracks * sides * trackBlockSize;
    var image = new byte[totalSize];

    // Disk Info Block
    var magic = "MV - CPCEMU Disk-File\r\nDisk-Info\r\n";
    Encoding.ASCII.GetBytes(magic).CopyTo(image, 0);
    image[48] = (byte)tracks;
    image[49] = (byte)sides;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(50), (ushort)trackBlockSize);

    var trackOffset = 256;
    var fillIdx = 0;
    for (var t = 0; t < tracks; t++) {
      for (var s = 0; s < sides; s++) {
        // Track Info Block
        Encoding.ASCII.GetBytes("Track-Info\r\n").CopyTo(image, trackOffset);
        image[trackOffset + 12] = 0;
        image[trackOffset + 16] = (byte)t;
        image[trackOffset + 17] = (byte)s;
        image[trackOffset + 20] = (byte)sizeCode;
        image[trackOffset + 21] = (byte)sectorsPerTrack;
        image[trackOffset + 22] = 0x4E;
        image[trackOffset + 23] = 0xE5;

        // Sector info table
        for (var i = 0; i < sectorsPerTrack; i++) {
          var si = trackOffset + 24 + i * 8;
          image[si + 0] = (byte)t;
          image[si + 1] = (byte)s;
          image[si + 2] = (byte)(0xC1 + i);
          image[si + 3] = (byte)sizeCode;
        }

        // Sector data area
        var sectorDataBase = trackOffset + 256;
        for (var i = 0; i < sectorsPerTrack; i++) {
          var dest = sectorDataBase + i * sectorSize;
          if (sectorFills != null && fillIdx < sectorFills.Length) {
            var fill = sectorFills[fillIdx++];
            fill.AsSpan(0, Math.Min(fill.Length, sectorSize)).CopyTo(image.AsSpan(dest));
          }
          // else leave as zero
        }

        trackOffset += trackBlockSize;
      }
    }

    return image;
  }

  /// <summary>
  /// Builds a minimal Extended CPC DSK image.
  /// </summary>
  private static byte[] BuildExtendedDsk(int tracks, int sides, int sectorsPerTrack, int sectorSize) {
    var sizeCode = SizeCode(sectorSize);
    var trackBlockSize = 256 + sectorsPerTrack * sectorSize;

    // Disk Info Block (256 bytes)
    var image = new List<byte>(new byte[256]);
    var magic = "EXTENDED CPC DSK File\r\nDisk-Info\r\n";
    var magicBytes = Encoding.ASCII.GetBytes(magic);
    for (var i = 0; i < magicBytes.Length; i++) image[i] = magicBytes[i];
    image[48] = (byte)tracks;
    image[49] = (byte)sides;

    // Track size table at offset 52: high byte of each track block size
    var highByte = (byte)(trackBlockSize / 256);
    for (var i = 0; i < tracks * sides; i++)
      image[52 + i] = highByte;

    // Track blocks
    for (var t = 0; t < tracks; t++) {
      for (var s = 0; s < sides; s++) {
        var tib = new byte[256];
        Encoding.ASCII.GetBytes("Track-Info\r\n").CopyTo(tib, 0);
        tib[12] = 0;
        tib[16] = (byte)t;
        tib[17] = (byte)s;
        tib[20] = (byte)sizeCode;
        tib[21] = (byte)sectorsPerTrack;
        tib[22] = 0x4E;
        tib[23] = 0xE5;
        for (var i = 0; i < sectorsPerTrack; i++) {
          var si = 24 + i * 8;
          tib[si + 0] = (byte)t;
          tib[si + 1] = (byte)s;
          tib[si + 2] = (byte)(0xC1 + i);
          tib[si + 3] = (byte)sizeCode;
          // Extended: actual size at bytes 6-7
          BinaryPrimitives.WriteUInt16LittleEndian(tib.AsSpan(si + 6), (ushort)sectorSize);
        }
        image.AddRange(tib);
        // Sector data (all zeros)
        image.AddRange(new byte[sectorsPerTrack * sectorSize]);
      }
    }

    return [.. image];
  }

  private static int SizeCode(int sectorSize) {
    var code = 0; var sz = 128;
    while (sz < sectorSize && code < 7) { sz <<= 1; code++; }
    return code;
  }

  // ── Writer helpers ─────────────────────────────────────────────────────

  private static byte[] WriteAndRead(Action<CpcDskWriter> addFiles,
      int tracks = 5, int sides = 1, int sectorsPerTrack = 9, int sectorSize = 512) {
    using var ms = new MemoryStream();
    using (var w = new CpcDskWriter(ms, leaveOpen: true, tracks, sides, sectorsPerTrack, sectorSize)) {
      addFiles(w);
      w.Finish();
    }
    ms.Position = 0;
    return ms.ToArray();
  }

  // ── Round-trip tests ───────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello Amstrad CPC!"u8.ToArray();

    var image = WriteAndRead(w => w.AddFile("hello.txt", data));

    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    // At least one sector entry should exist
    Assert.That(r.Entries, Is.Not.Empty);
    // The disk should be Standard format
    Assert.That(r.IsExtended, Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var file1 = "First file data"u8.ToArray();
    var file2 = "Second file data"u8.ToArray();
    var file3 = "Third file data"u8.ToArray();

    var image = WriteAndRead(w => {
      w.AddFile("file1.txt", file1);
      w.AddFile("file2.txt", file2);
      w.AddFile("file3.txt", file3);
    });

    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    // All tracks * sides * sectorsPerTrack sectors should be present
    Assert.That(r.Entries.Count, Is.EqualTo(5 * 1 * 9));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_SectorData_IsStored() {
    // Write a file smaller than one sector; verify the raw sector bytes contain the data
    var payload = new byte[64];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i + 1);

    var image = WriteAndRead(w => w.AddFile("data.bin", payload));

    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    // Find track 1 sector (where file data starts)
    var track1Entries = r.Entries.Where(e => e.Track == 1).ToList();
    Assert.That(track1Entries, Is.Not.Empty);

    var first = track1Entries[0];
    var extracted = r.Extract(first);
    // The first 64 bytes of the sector should match payload
    Assert.That(extracted.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyDisk() {
    var image = WriteAndRead(w => { /* no files */ });

    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    Assert.That(r.Tracks, Is.EqualTo(5));
    Assert.That(r.Sides, Is.EqualTo(1));
    Assert.That(r.Entries.Count, Is.EqualTo(5 * 1 * 9));
  }

  // ── Standard format parsing ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Reader_StandardFormat_Geometry() {
    var image = BuildStandardDsk(tracks: 3, sides: 1, sectorsPerTrack: 4, sectorSize: 512);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    Assert.That(r.IsExtended, Is.False);
    Assert.That(r.Tracks, Is.EqualTo(3));
    Assert.That(r.Sides, Is.EqualTo(1));
    Assert.That(r.Entries.Count, Is.EqualTo(3 * 1 * 4));
  }

  [Test, Category("HappyPath")]
  public void Reader_StandardFormat_EntryNames() {
    var image = BuildStandardDsk(tracks: 2, sides: 1, sectorsPerTrack: 3, sectorSize: 512);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    // Track 0, side 0, sector IDs 0xC1..0xC3
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("T00S0_C1"));
    Assert.That(names, Does.Contain("T00S0_C2"));
    Assert.That(names, Does.Contain("T00S0_C3"));
    Assert.That(names, Does.Contain("T01S0_C1"));
  }

  [Test, Category("HappyPath")]
  public void Reader_StandardFormat_SectorProperties() {
    var image = BuildStandardDsk(tracks: 1, sides: 1, sectorsPerTrack: 2, sectorSize: 256);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    var e = r.Entries[0];
    Assert.That(e.Track,    Is.EqualTo(0));
    Assert.That(e.Side,     Is.EqualTo(0));
    Assert.That(e.SectorId, Is.EqualTo(0xC1));
    Assert.That(e.Size,     Is.EqualTo(256));
  }

  [Test, Category("HappyPath")]
  public void Reader_StandardFormat_ExtractSectorData() {
    var payload = new byte[512];
    for (var i = 0; i < 512; i++) payload[i] = (byte)(0xAB);

    var image = BuildStandardDsk(tracks: 1, sides: 1, sectorsPerTrack: 1, sectorSize: 512,
        sectorFills: [payload]);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Reader_StandardFormat_TwoSides() {
    var image = BuildStandardDsk(tracks: 2, sides: 2, sectorsPerTrack: 5, sectorSize: 512);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    Assert.That(r.Sides, Is.EqualTo(2));
    Assert.That(r.Entries.Count, Is.EqualTo(2 * 2 * 5));
    // Should have both side 0 and side 1 entries
    Assert.That(r.Entries.Any(e => e.Side == 1), Is.True);
  }

  // ── Extended format parsing ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Reader_ExtendedFormat_Geometry() {
    var image = BuildExtendedDsk(tracks: 3, sides: 1, sectorsPerTrack: 4, sectorSize: 512);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    Assert.That(r.IsExtended, Is.True);
    Assert.That(r.Tracks, Is.EqualTo(3));
    Assert.That(r.Sides, Is.EqualTo(1));
    Assert.That(r.Entries.Count, Is.EqualTo(3 * 1 * 4));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtendedFormat_SectorSize_FromActualSizeField() {
    // Extended format reads sector sizes from the per-sector uint16 field
    var image = BuildExtendedDsk(tracks: 1, sides: 1, sectorsPerTrack: 2, sectorSize: 1024);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    foreach (var e in r.Entries)
      Assert.That(e.Size, Is.EqualTo(1024));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtendedFormat_EntryNames() {
    var image = BuildExtendedDsk(tracks: 1, sides: 1, sectorsPerTrack: 2, sectorSize: 512);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("T00S0_C1"));
    Assert.That(names, Does.Contain("T00S0_C2"));
  }

  // ── Descriptor tests ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new CpcDskFormatDescriptor();
    Assert.That(d.Id,               Is.EqualTo("CpcDsk"));
    Assert.That(d.DisplayName,      Is.EqualTo("CPC DSK"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".dsk"));
    Assert.That(d.Description,      Is.EqualTo("Amstrad CPC disk image"));
    Assert.That(d.Extensions,       Does.Contain(".dsk"));
    Assert.That(d.MagicSignatures,  Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsEntries() {
    var image = BuildStandardDsk(tracks: 2, sides: 1, sectorsPerTrack: 3, sectorSize: 512);
    using var ms = new MemoryStream(image);
    var entries = new CpcDskFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(2 * 1 * 3));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ThenList() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, "Sample content"u8.ToArray());
      var desc = new CpcDskFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "sample.txt", false)],
          new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Is.Not.Empty);
    } finally {
      File.Delete(tmpFile);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_WritesFiles() {
    var image = BuildStandardDsk(tracks: 1, sides: 1, sectorsPerTrack: 2, sectorSize: 512,
        sectorFills: [new byte[512], new byte[512]]);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(image);
      new CpcDskFormatDescriptor().Extract(ms, tmp, null, null);
      var written = Directory.GetFiles(tmp, "*", SearchOption.AllDirectories);
      Assert.That(written.Length, Is.EqualTo(2));
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  // ── Error handling tests ───────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void BadMagic_Throws() {
    var bad = new byte[512];
    Array.Fill(bad, (byte)0x42); // "BBBB..."
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => new CpcDskReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void TooSmall_Throws() {
    var tiny = new byte[64];
    using var ms = new MemoryStream(tiny);
    Assert.Throws<InvalidDataException>(() => new CpcDskReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Finish_CalledTwice_Throws() {
    using var ms = new MemoryStream();
    var w = new CpcDskWriter(ms, leaveOpen: true, tracks: 2, sides: 1, sectorsPerTrack: 3, sectorSize: 512);
    w.Finish();
    Assert.Throws<InvalidOperationException>(() => w.Finish());
  }

  [Test, Category("ErrorHandling")]
  public void AddFile_AfterFinish_Throws() {
    using var ms = new MemoryStream();
    var w = new CpcDskWriter(ms, leaveOpen: true, tracks: 2, sides: 1, sectorsPerTrack: 3, sectorSize: 512);
    w.Finish();
    Assert.Throws<InvalidOperationException>(() => w.AddFile("late.txt", [1, 2, 3]));
  }
}
