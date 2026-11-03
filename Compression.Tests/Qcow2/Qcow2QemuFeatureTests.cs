using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Qcow2;

namespace Compression.Tests.Qcow2;

/// <summary>
/// Images QEMU wrote with the features the reader used to refuse: zstd-compressed clusters,
/// extended L2 entries (subclusters, including zero and unallocated ones), and backing files
/// (QCOW2 and raw, with and without extended L2). Each must read as the guest disk
/// <c>qemu-img convert -O raw</c> produced from it. The vectors and the raw hashes were made by
/// <see cref="Given_QemuTools_When_TheVectorsAreRegenerated_Then_TheyMatchTheCommittedOnes"/>
/// (qemu-img / qemu-io 8.2, Ubuntu 24.04 in WSL).
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class Qcow2QemuFeatureTests {

  /// <summary>Images, the backing files they need, and the guest-disk hash qemu-img produced.</summary>
  private static readonly (string Image, string[] Backing, string Description)[] Vectors = [
    ("zstd.qcow2", [], "zstd-compressed clusters (compression_type=zstd)"),
    ("extl2.qcow2", [], "extended L2: allocated, zero and unallocated subclusters"),
    ("child.qcow2", ["base.qcow2"], "QCOW2 backing file"),
    ("child-extl2.qcow2", ["base.qcow2"], "extended L2 over a backing file: partial subclusters"),
    ("child-raw.qcow2", ["base.raw"], "raw backing file"),
  ];

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Qcow2Vectors." + name)
      ?? throw new InvalidOperationException($"Missing embedded vector {name}.");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static Dictionary<string, string> Manifest()
    => System.Text.Encoding.UTF8.GetString(Resource("qemu-raw.sha256"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split("  "))
      .ToDictionary(f => f[1], f => f[0]);

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  /// <summary>Writes the image and its backing files into a fresh directory and opens the image from there.</summary>
  private static byte[] ReadGuest(string image, string[] backing) {
    var dir = Directory.CreateTempSubdirectory("cwb-qcow2-").FullName;
    try {
      foreach (var name in backing.Append(image))
        File.WriteAllBytes(Path.Combine(dir, name), Resource(name));
      using var file = File.OpenRead(Path.Combine(dir, image));
      using var reader = new Qcow2Reader(file);
      return reader.ExtractDisk();
    } finally {
      Directory.Delete(dir, true);
    }
  }

  private static IEnumerable<TestCaseData> Cases()
    => Vectors.Select(v => new TestCaseData(v.Image, v.Backing).SetName($"QEMU {v.Description} reads as qemu-img converts it"));

  [TestCaseSource(nameof(Cases))]
  public void Given_AQemuImage_When_TheGuestDiskIsRead_Then_ItMatchesQemuImgsRawConversion(string image, string[] backing)
    => Assert.That(Sha256(ReadGuest(image, backing)), Is.EqualTo(Manifest()[image]));

  [Test]
  public void Given_AChildImageInMemory_When_Extracted_Then_TheMissingBackingFileIsReportedNotZeroFilled() {
    using var stream = new MemoryStream(Resource("child.qcow2"));
    using var reader = new Qcow2Reader(stream);
    Assert.Throws<NotSupportedException>(() => reader.ExtractDisk());
  }

  [Test]
  public void Given_AChildWhoseBackingFileIsMissing_When_Extracted_Then_ItIsRefused() {
    var dir = Directory.CreateTempSubdirectory("cwb-qcow2-").FullName;
    try {
      File.WriteAllBytes(Path.Combine(dir, "child.qcow2"), Resource("child.qcow2"));
      using var file = File.OpenRead(Path.Combine(dir, "child.qcow2"));
      using var reader = new Qcow2Reader(file);
      Assert.Throws<InvalidDataException>(() => reader.ExtractDisk());
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_ADirtyImage_When_Read_Then_TheGuestDiskIsUnchanged() {
    var image = Resource("zstd.qcow2");
    image[79] |= 0x01; // incompatible_features bit 0: dirty (refcounts may be stale; data is not)
    using var reader = new Qcow2Reader(new MemoryStream(image));
    Assert.That(Sha256(reader.ExtractDisk()), Is.EqualTo(Manifest()["zstd.qcow2"]));
  }

  [TestCase(0x04, Description = "external data file")]
  [TestCase(0x20, Description = "an unknown incompatible feature")]
  public void Given_AnUnsupportedIncompatibleFeature_When_Opened_Then_ItIsRefused(int bit) {
    var image = Resource("zstd.qcow2");
    image[79] |= (byte)bit;
    Assert.Throws<NotSupportedException>(() => _ = new Qcow2Reader(new MemoryStream(image)));
  }

  [Test]
  public void Given_ImagesWithNewFeatures_When_OpenedForWriting_Then_TheStreamIsReadOnly() {
    foreach (var image in new[] { "zstd.qcow2", "extl2.qcow2" }) {
      using var stream = new MemoryStream();
      stream.Write(Resource(image));
      using var guest = Qcow2Stream.Open(stream);
      Assert.That(guest.CanWrite, Is.False, image);
    }
  }

  /// <summary>
  /// Rebuilds every vector with qemu-img / qemu-io in WSL and checks that the committed vectors
  /// and hashes still describe what QEMU makes; with <c>CWB_QCOW2_WRITE_VECTORS=1</c> it writes them.
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_QemuTools_When_TheVectorsAreRegenerated_Then_TheyMatchTheCommittedOnes() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("qemu-img") || !FsInteropToolbox.WslHasTool("qemu-io"))
      Assert.Ignore("qemu-img and qemu-io are needed in WSL (sudo apt install -y qemu-utils).");

    var dir = Directory.CreateTempSubdirectory("cwb-qcow2-gen-").FullName;
    try {
      // 256 KiB guest: text-like bytes, a zero gap and a pseudo-random block.
      var guest = new byte[256 * 1024];
      var text = "qcow2 zstd cluster "u8;
      for (var i = 0; i < 131072; ++i)
        guest[i] = text[i % text.Length];
      new Random(7).NextBytes(guest.AsSpan(196608));
      File.WriteAllBytes(Path.Combine(dir, "guest.raw"), guest);
      var w = FsInteropToolbox.WinToWsl(dir);
      var script = string.Join(" && ", [
        $"cd {w}",
        "qemu-img convert -c -f raw -O qcow2 -o compression_type=zstd,cluster_size=4096 guest.raw zstd.qcow2",
        "qemu-img create -q -f qcow2 -o extended_l2=on,cluster_size=16384 extl2.qcow2 256K",
        "qemu-io -f qcow2 -c 'write -P 0x11 0 2048' -c 'write -P 0x22 5120 1024' -c 'write -z 16384 4096' -c 'write -P 0x33 40960 16384' -c 'write -z 65536 16384' extl2.qcow2",
        "qemu-img create -q -f qcow2 -o cluster_size=4096 base.qcow2 256K",
        "qemu-io -f qcow2 -c 'write -P 0xAA 0 65536' -c 'write -P 0xBB 131072 8192' base.qcow2",
        "qemu-img create -q -f qcow2 -b base.qcow2 -F qcow2 -o cluster_size=4096 child.qcow2 256K",
        "qemu-io -f qcow2 -c 'write -P 0x55 8192 4096' -c 'write -z 135168 4096' child.qcow2",
        "qemu-img create -q -f qcow2 -b base.qcow2 -F qcow2 -o extended_l2=on,cluster_size=16384 child-extl2.qcow2 256K",
        "qemu-io -f qcow2 -c 'write -P 0x66 1024 512' -c 'write -P 0x77 33792 1024' child-extl2.qcow2",
        "cp guest.raw base.raw",
        "qemu-img create -q -f qcow2 -b base.raw -F raw -o cluster_size=4096 child-raw.qcow2 256K",
        "qemu-io -f qcow2 -c 'write -P 0x99 4096 4096' child-raw.qcow2",
        "for f in zstd extl2 child child-extl2 child-raw; do qemu-img check -q $f.qcow2 && qemu-img convert -O raw $f.qcow2 $f.out; done",
        "for f in zstd extl2 child child-extl2 child-raw; do printf '%s  %s.qcow2\\n' $(sha256sum < $f.out | cut -c1-64) $f; done > qemu-raw.sha256",
      ]);
      var (stdout, stderr, code) = FsInteropToolbox.RunWsl(script);
      Assert.That(code, Is.Zero, stdout + stderr);

      var regenerated = File.ReadAllText(Path.Combine(dir, "qemu-raw.sha256"));
      if (Environment.GetEnvironmentVariable("CWB_QCOW2_WRITE_VECTORS") == "1") {
        var vectors = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Qcow2", "ReferenceVectors");
        Directory.CreateDirectory(vectors);
        foreach (var name in new[] { "zstd.qcow2", "extl2.qcow2", "base.qcow2", "child.qcow2", "child-extl2.qcow2", "base.raw", "child-raw.qcow2", "qemu-raw.sha256" })
          File.Copy(Path.Combine(dir, name), Path.Combine(vectors, name), overwrite: true);
        Assert.Pass("Vectors written; rebuild to embed them.");
      }

      // The guest disks QEMU produces must be the ones the committed vectors decode to.
      Assert.That(regenerated.Replace("\r\n", "\n"),
        Is.EqualTo(System.Text.Encoding.UTF8.GetString(Resource("qemu-raw.sha256")).Replace("\r\n", "\n")));
      // And our reader agrees with qemu-img on the freshly made images too.
      var manifest = regenerated.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split("  ")).ToDictionary(f => f[1], f => f[0]);
      foreach (var (image, _, _) in Vectors) {
        using var file = File.OpenRead(Path.Combine(dir, image));
        using var reader = new Qcow2Reader(file);
        Assert.That(Sha256(reader.ExtractDisk()), Is.EqualTo(manifest[image]), image);
      }
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
