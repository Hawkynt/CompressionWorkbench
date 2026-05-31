namespace Compression.Tests.Ntfs;

using System.Buffers.Binary;
using FileSystem.Ntfs;

/// <summary>
/// Tests for the tunable + auto-optimised cluster size and MFT record size
/// added to <see cref="NtfsWriter"/> and surfaced through
/// <see cref="NtfsFormatDescriptor"/>'s options schema.
/// </summary>
[TestFixture]
public class NtfsTunableGeometryTests {

  private static byte[] MakeBig(int size) {
    var b = new byte[size];
    for (var i = 0; i < b.Length; i++) b[i] = (byte)(i % 256);
    return b;
  }

  private static byte[] ExtractByName(NtfsReader r, string name) {
    var e = r.Entries.First(x => x.Name == name);
    return r.Extract(e);
  }

  [Test, Category("RoundTrip")]
  public void Build_ExplicitEightKbCluster_RoundTrips() {
    var big = MakeBig(20000);
    var w = new NtfsWriter();
    w.AddFile("big.bin", big);
    w.AddFile("small.txt", "hi"u8.ToArray());
    var img = w.Build(8 * 1024 * 1024, 8192, 1024);

    // Boot sector sectors-per-cluster = 8192/512 = 16.
    Assert.That(img[13], Is.EqualTo(16));

    using var r = new NtfsReader(new MemoryStream(img));
    Assert.That(ExtractByName(r, "big.bin"), Is.EqualTo(big));
    Assert.That(ExtractByName(r, "small.txt"), Is.EqualTo("hi"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Build_Explicit2048MftRecord_RoundTrips() {
    var big = MakeBig(15000);
    var w = new NtfsWriter();
    w.AddFile("big.bin", big);
    var img = w.Build(8 * 1024 * 1024, 4096, 2048);

    // record (2048) < cluster (4096) → field = -log2(2048) = -11.
    Assert.That((sbyte)img[64], Is.EqualTo(-11));

    using var r = new NtfsReader(new MemoryStream(img));
    Assert.That(ExtractByName(r, "big.bin"), Is.EqualTo(big));
  }

  [Test, Category("RealWorld")]
  public void Build_MftRecordEqualsCluster_UsesPositiveCount() {
    var big = MakeBig(9000);
    var w = new NtfsWriter();
    w.AddFile("big.bin", big);
    var img = w.Build(8 * 1024 * 1024, 4096, 4096);

    // record (4096) >= cluster (4096) → field = 4096/4096 = 1 (positive count).
    Assert.That((sbyte)img[64], Is.EqualTo(1));

    using var r = new NtfsReader(new MemoryStream(img));
    Assert.That(ExtractByName(r, "big.bin"), Is.EqualTo(big));
  }

  [Test, Category("RoundTrip")]
  public void BuildAutoSized_RoundTrips() {
    var big = MakeBig(30000);
    var w = new NtfsWriter();
    w.AddFile("big.bin", big);
    w.AddFile("tiny.txt", "tiny"u8.ToArray());
    var img = w.BuildAutoSized();

    using var r = new NtfsReader(new MemoryStream(img));
    Assert.That(ExtractByName(r, "big.bin"), Is.EqualTo(big));
    Assert.That(ExtractByName(r, "tiny.txt"), Is.EqualTo("tiny"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void BuildAutoSized_HonoursExplicitGeometry() {
    var big = MakeBig(20000);
    var w = new NtfsWriter();
    w.AddFile("big.bin", big);
    var img = w.BuildAutoSized(requestedClusterSize: 8192, requestedMftRecordSize: 2048);

    Assert.That(img[13], Is.EqualTo(16));         // 8192/512
    Assert.That((sbyte)img[64], Is.EqualTo(-11)); // 2048 < 8192 → -log2(2048)

    using var r = new NtfsReader(new MemoryStream(img));
    Assert.That(ExtractByName(r, "big.bin"), Is.EqualTo(big));
  }

  [Test, Category("ErrorHandling")]
  public void Build_InvalidClusterSize_Throws() {
    var w = new NtfsWriter();
    Assert.That(() => w.Build(4 * 1024 * 1024, 3000, 1024),
      Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Test, Category("ErrorHandling")]
  public void Build_InvalidMftRecordSize_Throws() {
    var w = new NtfsWriter();
    Assert.That(() => w.Build(4 * 1024 * 1024, 4096, 1536),
      Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesClusterAndMftRecordSchema() {
    var descriptor = new NtfsFormatDescriptor();
    Assert.That(descriptor, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)descriptor).OptionsSchema;
    var keys = schema.Select(o => o.Key).ToList();

    Assert.That(keys, Does.Contain("ClusterSize"));
    Assert.That(keys, Does.Contain("MftRecordSize"));

    var cluster = schema.First(o => o.Key == "ClusterSize");
    Assert.That(cluster.AllowedValues, Does.Contain("Auto"));
    Assert.That(cluster.AllowedValues, Does.Contain("4 KB"));

    var mft = schema.First(o => o.Key == "MftRecordSize");
    Assert.That(mft.AllowedValues, Does.Contain("Auto"));
    Assert.That(mft.AllowedValues, Does.Contain("512 B"));
    Assert.That(mft.AllowedValues, Does.Contain("4 KB"));
  }

  [Test, Category("RoundTrip")]
  public void Create_WithExplicitClusterViaFormatSpecific_RoundTrips() {
    var descriptor = new NtfsFormatDescriptor();
    var temp = Path.GetTempFileName();
    try {
      var content = new string('x', 9000);
      File.WriteAllText(temp, content);
      var inputs = new List<Compression.Registry.ArchiveInputInfo> {
        new(temp, "data.txt", false),
      };
      using var output = new MemoryStream();
      descriptor.Create(output, inputs, new Compression.Registry.FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["ClusterSize"] = "8 KB",
          ["MftRecordSize"] = "2 KB",
          ["ImageSize"] = "16 MB",
        },
      });
      var img = output.ToArray();

      Assert.That(img[13], Is.EqualTo(16));         // 8 KB cluster → 16 sectors
      Assert.That((sbyte)img[64], Is.EqualTo(-11)); // 2 KB record < 8 KB cluster

      using var r = new NtfsReader(new MemoryStream(img));
      var data = ExtractByName(r, "data.txt");
      Assert.That(System.Text.Encoding.UTF8.GetString(data), Is.EqualTo(content));
    } finally {
      File.Delete(temp);
    }
  }
}
