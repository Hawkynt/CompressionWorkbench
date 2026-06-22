using System.Text;
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// NTFS LZNT1 per-file compression. When compression is enabled a non-resident
/// file's $DATA is stored as an NTFS compressed attribute: the 0x0001 compressed
/// flag in the attribute header, a 16-cluster compression unit, and sparse runs
/// for the clusters each unit saves. The compressed file must read back
/// byte-for-byte, must be flagged compressed on disk, and — when the payload is
/// compressible — must occupy fewer allocated clusters than the uncompressed
/// equivalent. Compression off (the default) must leave the layout unchanged.
/// </summary>
[TestFixture]
public class NtfsCompressionTests {

  // 8 KiB of repeating text — highly compressible (LZNT1 collapses the runs).
  private static readonly byte[] CompressibleData =
    Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 200)))
      .AsSpan(0, 8 * 1024).ToArray();

  [Test, Category("RoundTrip")]
  public void CompressedFile_RoundTripsByteIdentical() {
    var w = new NtfsWriter();
    w.SetCompression(true);
    w.AddFile("repeating.txt", CompressibleData);
    var disk = w.Build(8 * 1024 * 1024);

    using var ms = new MemoryStream(disk);
    var r = new NtfsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "repeating.txt");

    Assert.That(entry.Size, Is.EqualTo(CompressibleData.Length), "logical size is the uncompressed length");
    Assert.That(r.Extract(entry), Is.EqualTo(CompressibleData), "compressed file content round-trips byte-identical");
  }

  [Test, Category("HappyPath")]
  public void CompressedFile_DataAttributeIsFlaggedCompressed() {
    var w = new NtfsWriter();
    w.SetCompression(true);
    w.AddFile("repeating.txt", CompressibleData);
    var disk = w.Build(8 * 1024 * 1024);

    var record = MftInspector.FindRecordByFileName(disk, "repeating.txt");
    Assert.That(MftInspector.DataAttributeIsResident(record), Is.False, "compressed file is non-resident");
    Assert.That(MftInspector.DataAttributeIsCompressed(record), Is.True,
      "compressed file's $DATA carries the 0x0001 compressed flag");
  }

  [Test, Category("HappyPath")]
  public void CompressedFile_AllocatesFewerClustersThanUncompressed() {
    // 64 KiB of repeating text — one full compression unit. LZNT1 collapses it
    // well below the 16 uncompressed clusters, so the saved clusters surface as
    // a sparse tail in the unit (the granularity of the 8 KiB sample is too
    // coarse to drop a whole 4 KiB cluster, hence a larger payload here).
    var payload = Encoding.ASCII.GetBytes(
        string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 2000)))
      .AsSpan(0, 64 * 1024).ToArray();

    var compressed = new NtfsWriter();
    compressed.SetCompression(true);
    compressed.AddFile("repeating.txt", payload);
    var compressedDisk = compressed.Build(8 * 1024 * 1024);

    var plain = new NtfsWriter();
    plain.AddFile("repeating.txt", payload);
    var plainDisk = plain.Build(8 * 1024 * 1024);

    var compressedRecord = MftInspector.FindRecordByFileName(compressedDisk, "repeating.txt");
    var plainRecord = MftInspector.FindRecordByFileName(plainDisk, "repeating.txt");

    var compressedClusters = MftInspector.DataRealClusterCount(compressedRecord);
    var plainClusters = MftInspector.DataRealClusterCount(plainRecord);

    Assert.That(compressedClusters, Is.GreaterThan(0), "compressed file still allocates its compressed clusters");
    Assert.That(compressedClusters, Is.LessThan(plainClusters),
      "LZNT1 compression of repeating text allocates fewer clusters than the uncompressed equivalent");
  }

  [Test, Category("RoundTrip")]
  public void MultiUnitCompressedFile_RoundTrips() {
    // 200 KiB spans multiple 64 KiB compression units (16 clusters * 4 KiB),
    // exercising the per-unit windowing on both the writer and reader.
    var data = Encoding.ASCII.GetBytes(
        string.Concat(Enumerable.Repeat("compression-unit-boundary-payload-0123456789 ", 6000)))
      .AsSpan(0, 200 * 1024).ToArray();

    var w = new NtfsWriter();
    w.SetCompression(true);
    w.AddFile("big.txt", data);
    var disk = w.Build(16 * 1024 * 1024);

    using var ms = new MemoryStream(disk);
    var r = new NtfsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "big.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(data), "multi-unit compressed file round-trips byte-identical");
  }

  [Test, Category("RoundTrip")]
  public void IncompressibleFile_StillRoundTrips() {
    // Pseudo-random bytes do not compress; each unit is stored raw (no sparse
    // tail) but the attribute is still flagged compressed and must round-trip.
    var rng = new Random(1234);
    var data = new byte[100 * 1024];
    rng.NextBytes(data);

    var w = new NtfsWriter();
    w.SetCompression(true);
    w.AddFile("random.bin", data);
    var disk = w.Build(16 * 1024 * 1024);

    using var ms = new MemoryStream(disk);
    var r = new NtfsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "random.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(data), "incompressible compressed file round-trips byte-identical");
  }

  [Test, Category("HappyPath")]
  public void CompressionOff_IsDefault_AndLeavesDataUncompressed() {
    var w = new NtfsWriter();
    // No SetCompression call → default off.
    w.AddFile("repeating.txt", CompressibleData);
    var disk = w.Build(8 * 1024 * 1024);

    var record = MftInspector.FindRecordByFileName(disk, "repeating.txt");
    Assert.That(MftInspector.DataAttributeIsCompressed(record), Is.False,
      "default build does not flag $DATA compressed");

    using var ms = new MemoryStream(disk);
    var r = new NtfsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "repeating.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(CompressibleData), "uncompressed file round-trips");
  }

  [Test, Category("HappyPath")]
  public void Create_CompressionLznt1_FromFormatSpecific_ProducesCompressedFile() {
    var descriptor = new NtfsFormatDescriptor();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, CompressibleData);
      var output = new MemoryStream();
      var options = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["Compression"] = "LZNT1",
          ["ImageSize"] = "16 MB",
        }
      };
      descriptor.Create(output, [new ArchiveInputInfo(tmp, "repeating.txt", false)], options);

      var image = output.ToArray();
      var record = MftInspector.FindRecordByFileName(image, "repeating.txt");
      Assert.That(MftInspector.DataAttributeIsCompressed(record), Is.True,
        "Compression=LZNT1 flows through Create to a compressed $DATA attribute");

      using var ms = new MemoryStream(image);
      var r = new NtfsReader(ms);
      var entry = r.Entries.Single(e => e.Name == "repeating.txt");
      Assert.That(r.Extract(entry), Is.EqualTo(CompressibleData), "created compressed file round-trips");
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("HappyPath")]
  public void Create_NtfsVersion30_StampsMinorVersionZero() {
    var descriptor = new NtfsFormatDescriptor();
    var output = new MemoryStream();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["NtfsVersion"] = "3.0" }
    };
    descriptor.Create(output, [], options);

    var image = output.ToArray();
    // $Volume is MFT record 3; its $VOLUME_INFORMATION (type 0x70) carries the
    // major/minor version at value offset +8/+9.
    var record = MftInspector.ReadRecord(image, 3);
    var (major, minor) = MftInspector.VolumeVersion(record);
    Assert.That(major, Is.EqualTo(3), "major version stays 3");
    Assert.That(minor, Is.EqualTo(0), "NtfsVersion=3.0 stamps minor version 0");
  }
}
