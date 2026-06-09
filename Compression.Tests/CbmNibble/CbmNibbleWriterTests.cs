#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.CbmNibble;

namespace Compression.Tests.CbmNibble;

/// <summary>
/// WORM-tier verification for the G64 v2 writer: header / speed-table /
/// track-offset table conformance, BAM + directory sanity, GCR encoding
/// round-trip, and end-to-end Descriptor.Create round-trip via the reader.
/// </summary>
[TestFixture]
public class CbmNibbleWriterTests {

  [Test, Category("HappyPath")]
  public void Build_EmitsCanonicalG64Header() {
    var w = new CbmNibbleWriter();
    w.AddFile("HELLO", Encoding.ASCII.GetBytes("hello cbm world"));
    var image = w.Build();

    // 0x00..0x07 "GCR-1541"
    Assert.That(image.AsSpan(0, 8).ToArray(), Is.EqualTo("GCR-1541"u8.ToArray()),
      "G64 magic must occupy bytes 0..7 of the header.");
    // 0x08 version = 0
    Assert.That(image[8], Is.EqualTo((byte)0),
      "G64 v2 header version byte at offset 8 is 0.");
    // 0x09 half-track count = 84
    Assert.That(image[9], Is.EqualTo((byte)CbmNibbleWriter.StandardHalfTrackCount),
      "Standard 1541 image uses 84 half-tracks (35 whole tracks).");
    // 0x0A..0x0B max track size
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(10, 2)),
      Is.EqualTo((ushort)CbmNibbleWriter.DefaultMaxTrackSize),
      "Header offset 10 must hold the maxTrackSize used to lay out the per-track payload regions.");
  }

  [Test, Category("HappyPath")]
  public void Build_SpeedTable_MatchesZoneAssignment() {
    var w = new CbmNibbleWriter();
    w.AddFile("X", new byte[256]);
    var image = w.Build();

    var speedTableOffset = 12 + CbmNibbleWriter.StandardHalfTrackCount * 4;

    // Track 1 (half-track index 0) — zone 3 (innermost).
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(speedTableOffset, 4)),
      Is.EqualTo(3u),
      "Track 1 lives in speed zone 3 per the 1541 ROM zone table.");
    // Track 18 (BAM/dir, half-track index 34) — zone 2.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(speedTableOffset + 34 * 4, 4)),
      Is.EqualTo(2u),
      "Track 18 (BAM/directory) lives in speed zone 2.");
    // Track 25 (half-track 48) — zone 1.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(speedTableOffset + 48 * 4, 4)),
      Is.EqualTo(1u),
      "Track 25 lives in speed zone 1.");
    // Track 35 (half-track 68) — zone 0.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(speedTableOffset + 68 * 4, 4)),
      Is.EqualTo(0u),
      "Track 35 lives in speed zone 0 (outermost).");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Build_RoundTrips_ThroughReader() {
    var w = new CbmNibbleWriter();
    w.AddFile("ALPHA", Encoding.ASCII.GetBytes("alpha cbm content"));
    var image = w.Build();

    var img = CbmNibbleReader.Read(image, "image.g64");
    Assert.That(img.Kind, Is.EqualTo(CbmNibbleReader.ImageKind.G64));
    Assert.That(img.TrackCount, Is.EqualTo(CbmNibbleWriter.StandardHalfTrackCount));
    // Track 0 (whole track 1) should have data; odd half-tracks should be empty (whole-track-step).
    Assert.That(img.Tracks[0].Data.Length, Is.GreaterThan(0),
      "Whole track 1 (half-track index 0) must carry GCR data.");
    Assert.That(img.Tracks[1].Data, Is.Empty,
      "Odd half-track 1 must be empty (whole-track-step image).");
    Assert.That(img.Tracks[34].Data.Length, Is.GreaterThan(0),
      "Track 18 (half-track 34) must carry the BAM + directory.");
  }

  [Test, Category("HappyPath")]
  public void Build_EncodedTrackData_ContainsSyncMarks() {
    var w = new CbmNibbleWriter();
    w.AddFile("X", new byte[256]);
    var image = w.Build();

    var img = CbmNibbleReader.Read(image, "image.g64");
    var track1 = img.Tracks[0].Data;
    // SYNC sequences (≥5 consecutive 0xFF) must appear at the start of every
    // header and data block — at least one in the first 256 bytes of track 1.
    var sawSync = false;
    var run = 0;
    foreach (var b in track1.AsSpan(0, Math.Min(256, track1.Length))) {
      if (b == 0xFF) { run++; if (run >= 5) { sawSync = true; break; } }
      else run = 0;
    }
    Assert.That(sawSync, Is.True,
      "Track 1 encoded payload must contain at least one SYNC mark (5+ × 0xFF) in the leading 256 bytes.");
  }

  [Test, Category("HappyPath")]
  public void GcrEncodeBlock_ProducesExpectedBitLength() {
    // 8 raw bytes (64 bits) → 8 × 10-bit GCR codes = 80 bits = 10 bytes.
    var raw = new byte[] { 0x00, 0xFF, 0x55, 0xAA, 0x12, 0x34, 0x56, 0x78 };
    var encoded = CbmNibbleWriter.GcrEncodeBlock(raw);
    Assert.That(encoded, Has.Length.EqualTo(10),
      "4-to-5 GCR encoding expands 8 raw bytes into exactly 10 encoded bytes.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_RoundTripsViaList() {
    var d = new G64FormatDescriptor();
    var alpha = Encoding.ASCII.GetBytes("alpha via descriptor.Create");
    var beta = new byte[600];
    for (var i = 0; i < beta.Length; i++) beta[i] = (byte)(i & 0xFF);

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("ALPHA", alpha),
      ArchiveInputInfo.InMemory("BETA", beta),
    };

    using var output = new MemoryStream();
    d.Create(output, inputs, new FormatCreateOptions());

    var image = output.ToArray();
    Assert.That(image.AsSpan(0, 8).ToArray(), Is.EqualTo("GCR-1541"u8.ToArray()),
      "Descriptor.Create must emit a G64 v2 image starting with the GCR-1541 magic.");

    // Reader surfaces metadata.ini + per-track payload — round-trip via descriptor.List.
    using var listMs = new MemoryStream(image);
    var entries = d.List(listMs, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True,
      "List must surface metadata.ini after a Create round-trip.");
    Assert.That(entries.Count(e => e.Name.StartsWith("track_", StringComparison.Ordinal)),
      Is.GreaterThan(0),
      "List must surface at least one populated track entry after a Create round-trip.");
  }

  [Test, Category("Sad")]
  public void AddFile_NullData_Throws() {
    var w = new CbmNibbleWriter();
    Assert.That(() => w.AddFile("X", null!), Throws.ArgumentNullException);
  }

  [Test, Category("Sad")]
  public void AddFile_EmptyName_Throws() {
    var w = new CbmNibbleWriter();
    Assert.That(() => w.AddFile("", new byte[1]), Throws.InstanceOf<ArgumentException>());
  }

  [Test, Category("Boundary")]
  public void Build_TooManyFiles_Throws() {
    // Tracks 1..17 host 17 × 21 = 357 sectors. 358 sectors must overflow.
    var w = new CbmNibbleWriter();
    // 357 single-sector files fit; the 358th must fail.
    for (var i = 0; i < 357; i++) w.AddFile("F" + i, new byte[1]);
    Assert.That(() => w.Build(), Throws.Nothing, "357 single-sector files fit on tracks 1..17.");

    var w2 = new CbmNibbleWriter();
    for (var i = 0; i < 358; i++) w2.AddFile("F" + i, new byte[1]);
    Assert.That(() => w2.Build(), Throws.InstanceOf<InvalidOperationException>(),
      "Overflow past 357 single-sector files must surface as a writer error.");
  }
}
