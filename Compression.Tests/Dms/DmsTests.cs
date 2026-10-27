using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Dms;

namespace Compression.Tests.Dms;

[TestFixture]
public class DmsTests {
  internal static string VectorPath(string name)
    => Path.Combine(FilesystemReadmeIsCurrentTestsRoot(), "Compression.Tests", "Dms", "ReferenceVectors", name);

  private static string FilesystemReadmeIsCurrentTestsRoot() => Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot();

  private static byte[] Write(Action<DmsWriter> body) {
    using var ms = new MemoryStream();
    using (var w = new DmsWriter(ms, leaveOpen: true))
      body(w);
    return ms.ToArray();
  }

  [Test, Category("RoundTrip")]
  public void GivenAStoredTrack_WhenRoundTripped_ThenIdentical() {
    var trackData = new byte[11264];
    new Random(1).NextBytes(trackData);
    var dms = Write(w => w.WriteTrack(0, trackData, compressionMode: 0));
    using var r = new DmsReader(new MemoryStream(dms));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Number, Is.EqualTo(0));
    Assert.That(r.Entries[0].Mode, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(trackData));
  }

  [Test, Category("RoundTrip")]
  public void GivenRunHeavyData_WhenRleCoded_ThenSmallerAndIdentical() {
    var trackData = new byte[11264];
    for (var i = 0; i < trackData.Length; ++i)
      trackData[i] = (byte)(i < 4000 ? 0 : i < 4300 ? 0x90 : i % 7 == 0 ? 0x90 : i & 0x0F);
    var dms = Write(w => w.WriteTrack(0, trackData, compressionMode: 1));
    using var r = new DmsReader(new MemoryStream(dms));
    Assert.That(r.Entries[0].Mode, Is.EqualTo(1));
    Assert.That(r.Entries[0].PackedSize, Is.LessThan(trackData.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(trackData));
  }

  [TestCase(1)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(254)]
  [TestCase(255)]
  [TestCase(256)]
  [TestCase(11264)]
  public void GivenARunOfLength_WhenRleCoded_ThenItDecodesBack(int run) {
    var data = new byte[11264];
    data.AsSpan(0, run).Fill(0x90);
    data.AsSpan(run).Fill(0x41);
    var dms = Write(w => w.WriteTrack(0, data, compressionMode: 1));
    using var r = new DmsReader(new MemoryStream(dms));
    Assert.That(r.ExtractDisk(), Is.EqualTo(data));
  }

  [Test]
  public void GivenADiskImage_WhenWritten_ThenHeaderIsRealDmsAndCylindersRoundTrip() {
    var image = new byte[11264 * 3 + 100];
    new Random(2).NextBytes(image);
    var dms = Write(w => w.WriteDisk(image, compressionMode: 1));
    Assert.That(dms.AsSpan(0, 4).SequenceEqual("DMS!"u8), Is.True);
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(dms.AsSpan(16)), Is.EqualTo(0));   // low track
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(dms.AsSpan(18)), Is.EqualTo(3));   // high track
    Assert.That(dms.AsSpan(56, 2).SequenceEqual("TR"u8), Is.True);
    using var r = new DmsReader(new MemoryStream(dms));
    Assert.That(r.ExtractDisk(), Is.EqualTo(image));
  }

  [Test]
  public void GivenTheHeavy2Sample_WhenDecoded_ThenEveryTrackChecksumHolds() {
    using var r = new DmsReader(File.OpenRead(VectorPath("Damage-head.dms")));
    Assert.That(r.Entries.Select(t => t.Mode), Is.All.EqualTo(6));
    var disk = r.ExtractDisk();
    Assert.That(disk, Has.Length.EqualTo(6 * 11264));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(disk)), Is.EqualTo(DmsExternalConformanceTests.ExpectedSha256["Damage-head.dms"]));
  }

  [Test]
  public void GivenTheMediumSample_WhenDecoded_ThenTheStateCarriesAcrossNoInitTracks() {
    using var r = new DmsReader(File.OpenRead(VectorPath("Genesis-head.dms")));
    Assert.That(r.Entries.Select(t => t.Mode).Distinct(), Is.EquivalentTo(new[] { 0, 1, 3 }));
    var disk = r.ExtractDisk();
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(disk)), Is.EqualTo(DmsExternalConformanceTests.ExpectedSha256["Genesis-head.dms"]));
  }

  [Test]
  public void GivenTheDescriptor_WhenListingASample_ThenDiskAndMetadataAppear() {
    using var fs = File.OpenRead(VectorPath("Damage-head.dms"));
    var names = new DmsFormatDescriptor().List(fs, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("disk.adf").And.Contain("metadata.ini"));
  }

  [Test]
  public void GivenACorruptedPayloadByte_WhenDecoded_ThenRefused() {
    var dms = File.ReadAllBytes(VectorPath("Damage-head.dms"));
    dms[56 + 20 + 100] ^= 0x01;
    using var r = new DmsReader(new MemoryStream(dms));
    Assert.Throws<InvalidDataException>(() => r.ExtractDisk());
  }

  [Test]
  public void GivenABadHeaderCrc_WhenOpened_ThenInvalidDataButListFallsBack() {
    var dms = File.ReadAllBytes(VectorPath("Damage-head.dms"));
    dms[20] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => new DmsReader(new MemoryStream(dms)));
    var listed = new DmsFormatDescriptor().List(new MemoryStream(dms), null);
    Assert.That(listed.Single().Name, Is.EqualTo("FULL.dms"));
  }

  [Test]
  public void GivenATruncatedTrack_WhenOpened_ThenInvalidData() {
    var dms = File.ReadAllBytes(VectorPath("Damage-head.dms"));
    Assert.Throws<InvalidDataException>(() => new DmsReader(new MemoryStream(dms[..^10])));
  }

  [Test]
  public void GivenNoMagic_WhenOpened_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => new DmsReader(new MemoryStream(new byte[100])));

  [Test]
  public void GivenAnEncryptedHeaderWithoutPassword_WhenExtracted_ThenRefused() {
    var dms = Write(w => w.WriteTrack(0, new byte[11264], 0));
    BinaryPrimitives.WriteUInt32BigEndian(dms.AsSpan(8), 2); // encrypted
    BinaryPrimitives.WriteUInt16BigEndian(dms.AsSpan(54), Crc(dms.AsSpan(4, 50)));
    using var r = new DmsReader(new MemoryStream(dms));
    Assert.Throws<UnauthorizedAccessException>(() => r.ExtractDisk());
  }

  [Test]
  public void GivenAnUnsupportedWriterMode_WhenWriting_ThenRefused()
    => Assert.Throws<NotSupportedException>(() => Write(w => w.WriteTrack(0, new byte[10], compressionMode: 6)));

  [Test]
  public void GivenTheDescriptor_WhenCreatingFromAnImage_ThenItRoundTrips() {
    var image = new byte[11264 * 2];
    new Random(3).NextBytes(image.AsSpan(0, 11264));
    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, image);
      using var ms = new MemoryStream();
      new DmsFormatDescriptor().Create(ms, [new ArchiveInputInfo(path, "disk.adf", false)], new FormatCreateOptions());
      ms.Position = 0;
      using var r = new DmsReader(ms, leaveOpen: true);
      Assert.That(r.ExtractDisk(), Is.EqualTo(image));
    } finally {
      File.Delete(path);
    }
  }

  private static ushort Crc(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (var b in data) {
      crc ^= b;
      for (var k = 0; k < 8; ++k)
        crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
    }
    return crc;
  }
}
