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

  // ── What the disk holds ────────────────────────────────────────────────
  //
  // These used to count sectors: a five-track, nine-sector disk was expected to
  // read back as forty-five entries called T00S0_C1 and so on. That is a true
  // description of the container and none at all of the disk, and it meant every
  // file written here was reported as missing while its bytes sat on the disk
  // perfectly intact. What a CPC reads is the AMSDOS directory, so that is what
  // these check.

  private static CpcDskReader ReadBack(byte[] image) {
    var ms = new MemoryStream(image);
    return new CpcDskReader(ms);
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello Amstrad CPC!"u8.ToArray();
    var image = WriteAndRead(w => w.AddFile("HELLO.TXT", data));

    var reader = ReadBack(image);
    Assert.That(reader.Entries.Select(e => e.Name), Is.EqualTo(new[] { "HELLO.TXT" }));
    // CP/M measures a file in 128-byte records, so it comes back rounded up.
    Assert.That(reader.Extract(reader.Entries[0]).AsSpan(0, data.Length).SequenceEqual(data), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var one = new byte[300];
    var two = new byte[2000];
    var three = new byte[40];
    for (var i = 0; i < one.Length; ++i) one[i] = (byte)(i * 7);
    for (var i = 0; i < two.Length; ++i) two[i] = (byte)(i * 11 + 3);
    for (var i = 0; i < three.Length; ++i) three[i] = (byte)(i * 13 + 5);

    var image = WriteAndRead(w => {
      w.AddFile("ONE.BIN", one);
      w.AddFile("TWO.BIN", two);
      w.AddFile("THREE.BIN", three);
    });

    var reader = ReadBack(image);
    Assert.That(reader.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal),
      Is.EqualTo(new[] { "ONE.BIN", "THREE.BIN", "TWO.BIN" }).AsCollection);

    foreach (var (name, want) in new[] { ("ONE.BIN", one), ("TWO.BIN", two), ("THREE.BIN", three) }) {
      var entry = reader.Entries.First(e => e.Name == name);
      Assert.That(reader.Extract(entry).AsSpan(0, want.Length).SequenceEqual(want), Is.True,
        $"'{name}' did not read back as written");
    }
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_FileLargerThanOneExtent_ChainsDirectoryEntries() {
    // One directory entry names sixteen blocks, so sixteen kilobytes. A longer
    // file needs another entry, and the entries have to be found and put back in
    // extent order or the file comes back as its first sixteen kilobytes.
    var data = new byte[40 * 1024];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i * 31 + (i >> 9));

    var image = WriteAndRead(w => w.AddFile("BIG.BIN", data), tracks: 40);
    var reader = ReadBack(image);

    var entry = reader.Entries.Single(e => e.Name == "BIG.BIN");
    Assert.That(entry.Size, Is.GreaterThanOrEqualTo(data.Length));
    Assert.That(reader.Extract(entry).AsSpan(0, data.Length).SequenceEqual(data), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyDisk() {
    var image = WriteAndRead(_ => { });
    Assert.That(ReadBack(image).Entries, Is.Empty);
  }

  // ── The directory a CPC would read ─────────────────────────────────────

  [Test, Category("Regression")]
  public void Directory_MatchesTheAmsdosDataFormat() {
    var data = new byte[3000];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)i;
    var image = WriteAndRead(w => w.AddFile("PROG.BIN", data), tracks: 40);

    // The directory is blocks zero and one, which are the first four sectors.
    const int diskInfo = 256, trackInfo = 256, sectorSize = 512, sectorsPerTrack = 9;
    long SectorOffset(int logical) =>
      diskInfo + (long)(logical / sectorsPerTrack) * (trackInfo + sectorsPerTrack * sectorSize)
      + trackInfo + (long)(logical % sectorsPerTrack) * sectorSize;

    var directory = new byte[4 * sectorSize];
    for (var s = 0; s < 4; ++s)
      Array.Copy(image, SectorOffset(s), directory, s * sectorSize, sectorSize);

    Assert.That(directory[0], Is.EqualTo(0), "user number of a live entry is zero");
    var name = System.Text.Encoding.ASCII.GetString(
      directory.AsSpan(1, 8).ToArray().Select(b => (byte)(b & 0x7F)).ToArray()).TrimEnd();
    Assert.That(name, Is.EqualTo("PROG"));

    var blocks = directory.AsSpan(16, 16).ToArray();
    // Blocks are numbered from the start of the disk and the directory holds the
    // first two, so a file's first block is two. Numbering them any other way
    // gives a CPC a directory that points somewhere else entirely.
    Assert.That(blocks[0], Is.EqualTo(2), "the first data block follows the directory");
    Assert.That(blocks[1], Is.EqualTo(3), "a file's blocks run on");
    Assert.That(blocks[2], Is.EqualTo(4));

    // Three kilobytes is three blocks, and the rest of the list must be zero:
    // the free-entry filler is 0xE5, and left in an allocation slot CP/M reads
    // that as block 229 and follows it into another file's bytes.
    for (var i = 3; i < 16; ++i)
      Assert.That(blocks[i], Is.EqualTo(0), $"allocation slot {i} should be empty, not 0x{blocks[i]:X2}");

    // Records are 128 bytes, so three thousand of them is twenty-four.
    Assert.That(directory[15], Is.EqualTo(24), "record count");
    Assert.That(directory[12], Is.EqualTo(0), "extent number");
  }

  [Test, Category("Regression")]
  public void Create_MoreThanTheDiskHolds_Refuses() {
    // 180 KB is 180 blocks, two of which are the directory. Silently dropping
    // what will not fit is how a disk comes back missing files with nothing to
    // say about it.
    using var ms = new MemoryStream();
    var writer = new CpcDskWriter(ms, leaveOpen: true, tracks: 40, sides: 1);
    for (var i = 0; i < 12; ++i) writer.AddFile($"F{i:D2}.BIN", new byte[32 * 1024]);

    Assert.Throws<InvalidOperationException>(() => writer.Finish());
  }

  // ── Container geometry ─────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Reader_StandardFormat_Geometry() {
    var image = BuildStandardDsk(tracks: 3, sides: 1, sectorsPerTrack: 4, sectorSize: 512);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    Assert.That(r.IsExtended, Is.False);
    Assert.That(r.Tracks, Is.EqualTo(3));
    Assert.That(r.Sides, Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Reader_StandardFormat_TwoSides() {
    var image = BuildStandardDsk(tracks: 2, sides: 2, sectorsPerTrack: 3, sectorSize: 512);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    Assert.That(r.Tracks, Is.EqualTo(2));
    Assert.That(r.Sides, Is.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtendedFormat_Geometry() {
    var image = BuildExtendedDsk(tracks: 3, sides: 1, sectorsPerTrack: 4, sectorSize: 512);
    using var ms = new MemoryStream(image);
    var r = new CpcDskReader(ms);

    Assert.That(r.IsExtended, Is.True);
    Assert.That(r.Tracks, Is.EqualTo(3));
    Assert.That(r.Sides, Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Reader_UnformattedDisk_HasNoFiles() {
    // A disk whose directory has never been written holds no files, and reading
    // one is not an error.
    var image = BuildStandardDsk(tracks: 5, sides: 1, sectorsPerTrack: 9, sectorSize: 512);
    using var ms = new MemoryStream(image);
    Assert.That(new CpcDskReader(ms).Entries, Is.Empty);
  }

  // ── Descriptor surface ─────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new CpcDskFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("CpcDsk"));
    Assert.That(d.DisplayName, Is.Not.Empty);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsTheFiles() {
    var image = WriteAndRead(w => {
      w.AddFile("A.BIN", new byte[100]);
      w.AddFile("B.BIN", new byte[100]);
    });

    using var ms = new MemoryStream(image);
    var entries = new CpcDskFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_WritesFiles() {
    var payload = new byte[600];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(i * 5 + 1);
    var image = WriteAndRead(w => w.AddFile("DATA.BIN", payload));

    var outDir = Path.Combine(Path.GetTempPath(), "cwb_cpc_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      using var ms = new MemoryStream(image);
      new CpcDskFormatDescriptor().Extract(ms, outDir, null, null);
      var written = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).ToList();
      Assert.That(written, Is.Not.Empty, "the extract wrote nothing");

      var got = File.ReadAllBytes(written.First(f => Path.GetFileName(f) == "DATA.BIN"));
      Assert.That(got.AsSpan(0, payload.Length).SequenceEqual(payload), Is.True);
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  // ── Error handling ─────────────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void BadMagic_Throws() {
    var bad = new byte[512];
    Array.Fill(bad, (byte)0x42);
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => new CpcDskReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[64]);
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
