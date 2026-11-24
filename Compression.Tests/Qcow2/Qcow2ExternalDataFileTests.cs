using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Qcow2;

namespace Compression.Tests.Qcow2;

/// <summary>
/// QCOW2 images whose guest data lives in an external data file (incompatible feature bit 2,
/// optionally declared a raw image by autoclear bit 1): images qemu-img and qemu-io wrote read
/// as <c>qemu-img convert -O raw</c> reads them; images our writer makes and edits our stream
/// makes pass <c>qemu-img check</c> and <c>qemu-img compare</c>.
/// </summary>
[TestFixture]
public sealed class Qcow2ExternalDataFileTests {

  /// <summary>Images, their data files, and what they cover (ReferenceVectors/README.md).</summary>
  private static readonly (string Image, string Data, string Description)[] Vectors = [
    ("datafile-raw.qcow2", "datafile-raw.img", "a raw external data file (data_file_raw=on)"),
    ("datafile.qcow2", "datafile.img", "an external data file with zero and unallocated clusters"),
  ];

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Qcow2Vectors." + name)
      ?? throw new InvalidOperationException($"Missing embedded vector {name}.");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static Dictionary<string, string> Manifest()
    => System.Text.Encoding.UTF8.GetString(Resource("qemu-datafile.sha256"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split("  "))
      .ToDictionary(f => f[1], f => f[0]);

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static string TempDir() => Directory.CreateTempSubdirectory("cwb-qcow2-data-").FullName;

  private static void Place(string dir, string image, string data) {
    File.WriteAllBytes(Path.Combine(dir, image), Resource(image));
    File.WriteAllBytes(Path.Combine(dir, data), Resource(data));
  }

  private static byte[] ReadGuest(string path) {
    using var file = File.OpenRead(path);
    using var reader = new Qcow2Reader(file);
    return reader.ExtractDisk();
  }

  private static byte[] Guest(int size, int seed) {
    var guest = new byte[size];
    var text = "external data file "u8;
    for (var i = 0; i < size / 2; ++i)
      guest[i] = text[i % text.Length];
    new Random(seed).NextBytes(guest.AsSpan(size * 3 / 4));
    return guest;
  }

  private static IEnumerable<TestCaseData> Cases()
    => Vectors.Select(v => new TestCaseData(v.Image, v.Data).SetName($"QEMU image with {v.Description} reads as qemu-img converts it"));

  [TestCaseSource(nameof(Cases))]
  [Category("ThirdPartyProof")]
  public void Given_AQemuImageWithADataFile_When_Read_Then_ItMatchesQemuImgsRawConversion(string image, string data) {
    var dir = TempDir();
    try {
      Place(dir, image, data);
      Assert.That(Sha256(ReadGuest(Path.Combine(dir, image))), Is.EqualTo(Manifest()[image]));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  [Category("ThirdPartyProof")]
  public void Given_AQemuImageWithADataFile_When_StreamedInOddPieces_Then_EveryByteMatches() {
    var dir = TempDir();
    try {
      Place(dir, "datafile.qcow2", "datafile.img");
      var path = Path.Combine(dir, "datafile.qcow2");
      var whole = ReadGuest(path);
      using var file = File.OpenRead(path);
      using var guest = Qcow2Stream.Open(file);
      var pieces = new byte[whole.Length];
      var at = 0;
      foreach (var step in Enumerable.Repeat(new[] { 1, 511, 4097, 3, 65536 }, 1000).SelectMany(s => s)) {
        if (at >= pieces.Length) break;
        var n = Math.Min(step, pieces.Length - at);
        guest.ReadExactly(pieces, at, n);
        at += n;
      }
      Assert.That(pieces, Is.EqualTo(whole));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  [Category("Exception")]
  public void Given_AnImageWithADataFileInMemory_When_Extracted_Then_ItIsRefusedNotZeroFilled() {
    using var reader = new Qcow2Reader(new MemoryStream(Resource("datafile-raw.qcow2")));
    Assert.Throws<NotSupportedException>(() => reader.ExtractDisk());
  }

  [Test]
  [Category("Exception")]
  public void Given_AnImageWhoseDataFileIsMissing_When_Extracted_Then_ItIsRefused() {
    var dir = TempDir();
    try {
      File.WriteAllBytes(Path.Combine(dir, "datafile-raw.qcow2"), Resource("datafile-raw.qcow2"));
      using var file = File.OpenRead(Path.Combine(dir, "datafile-raw.qcow2"));
      using var reader = new Qcow2Reader(file);
      Assert.Throws<InvalidDataException>(() => reader.ExtractDisk());
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  [Category("Exception")]
  public void Given_TheRawBitWithoutADataFile_When_Opened_Then_ItIsRefused() {
    var image = Resource("datafile-raw.qcow2");
    image[79] &= unchecked((byte)~0x04); // drop incompatible bit 2, keep autoclear bit 1
    Assert.Throws<InvalidDataException>(() => _ = new Qcow2Reader(new MemoryStream(image)));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_AQemuImageWithADataFileOpenedReadOnly_When_Opened_Then_TheStreamIsReadOnly() {
    var dir = TempDir();
    try {
      Place(dir, "datafile.qcow2", "datafile.img");
      using var file = File.OpenRead(Path.Combine(dir, "datafile.qcow2"));
      using var guest = Qcow2Stream.Open(file);
      Assert.That(guest.CanWrite, Is.False);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  /// <summary>
  /// Writes into a QEMU image with a data file: inside an allocated cluster, across a cluster
  /// boundary, into a zero cluster and into unallocated space past the written area.
  /// </summary>
  private static byte[] EditInPlace(string path, byte[] before) {
    var expected = (byte[])before.Clone();
    using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
    using var guest = Qcow2Stream.Open(file);
    Assert.That(guest.CanWrite, Is.True, "precondition: a data-file image is writable");
    foreach (var (offset, length, value) in new (int, int, byte)[] { (100, 300, 0xC1), (4090, 20, 0xC2), (20000, 5000, 0xC3), (200_000, 1, 0xC4) }) {
      var patch = Enumerable.Repeat(value, length).ToArray();
      guest.Position = offset;
      guest.Write(patch);
      patch.CopyTo(expected, offset);
    }
    guest.Flush();
    return expected;
  }

  [TestCase("datafile-raw.qcow2", "datafile-raw.img")]
  [TestCase("datafile.qcow2", "datafile.img")]
  [Category("RoundTrip")]
  public void Given_AQemuImageWithADataFile_When_EditedInPlace_Then_TheGuestHasTheEdits(string image, string data) {
    var dir = TempDir();
    try {
      Place(dir, image, data);
      var path = Path.Combine(dir, image);
      var expected = EditInPlace(path, ReadGuest(path));
      Assert.That(ReadGuest(path), Is.EqualTo(expected));
      Assert.That(new FileInfo(Path.Combine(dir, image)).Length, Is.EqualTo(Resource(image).Length),
        "guest data goes to the data file; the image itself does not grow");
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [TestCase(true)]
  [TestCase(false)]
  [Category("RoundTrip")]
  public void Given_OurImageWithADataFile_When_ReadBack_Then_TheGuestRoundTrips(bool raw) {
    var dir = TempDir();
    try {
      var guest = Guest(300_000, 11);
      var writer = new Qcow2Writer();
      writer.SetDiskImage(guest);
      writer.SetExternalDataFile("ours.img", raw);
      using (var image = File.Create(Path.Combine(dir, "ours.qcow2")))
      using (var data = File.Create(Path.Combine(dir, "ours.img")))
        writer.WriteTo(image, data);
      Assert.That(ReadGuest(Path.Combine(dir, "ours.qcow2")), Is.EqualTo(guest));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "ours.img")), Is.EqualTo(guest), "the data file is the raw guest disk");
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  [Category("Exception")]
  public void Given_AWriterWithADataFile_When_WrittenAsOneStream_Then_ItIsRefused() {
    var writer = new Qcow2Writer();
    writer.SetDiskImage(new byte[4096]);
    writer.SetExternalDataFile("x.img");
    Assert.Throws<InvalidOperationException>(() => writer.WriteTo(new MemoryStream()));
  }

  [Test]
  [Category("Exception")]
  public void Given_AWriterWithoutADataFile_When_WrittenWithOne_Then_ItIsRefused() {
    var writer = new Qcow2Writer();
    writer.SetDiskImage(new byte[4096]);
    Assert.Throws<InvalidOperationException>(() => writer.WriteTo(new MemoryStream(), new MemoryStream()));
  }

  [Test]
  [Category("Exception")]
  public void Given_APasswordAndADataFile_When_Written_Then_ItIsRefused() {
    var writer = new Qcow2Writer();
    writer.SetDiskImage(new byte[4096]);
    writer.SetPassword("pw", 1000);
    writer.SetExternalDataFile("x.img");
    Assert.Throws<NotSupportedException>(() => writer.WriteTo(new MemoryStream(), new MemoryStream()));
  }

  [TestCase("")]
  [Category("Boundary")]
  public void Given_AnEmptyDataFileName_When_Set_Then_ItIsRefused(string name)
    => Assert.Throws<ArgumentException>(() => new Qcow2Writer().SetExternalDataFile(name));

  [Test]
  [Category("Boundary")]
  public void Given_ADataFileNameOfTheLongestAndOneMoreByte_When_Set_Then_OnlyTheLongerIsRefused() {
    Assert.DoesNotThrow(() => new Qcow2Writer().SetExternalDataFile(new string('a', 1023)));
    Assert.Throws<ArgumentException>(() => new Qcow2Writer().SetExternalDataFile(new string('a', 1024)));
  }

  private static void RequireQemu() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("qemu-img") || !FsInteropToolbox.WslHasTool("qemu-io"))
      Assert.Ignore("qemu-img and qemu-io are needed in WSL (sudo apt install -y qemu-utils).");
  }

  /// <summary>qemu-img must find our data-file images consistent and read the guest we wrote, raw or not.</summary>
  [TestCase(true)]
  [TestCase(false)]
  [Category("ExternalInterop")]
  public void Given_OurImageWithADataFile_When_QemuImgChecksAndComparesIt_Then_ItAgrees(bool raw) {
    RequireQemu();
    var dir = TempDir();
    try {
      var guest = Guest(1 << 20, 12);
      File.WriteAllBytes(Path.Combine(dir, "guest.raw"), guest);
      var writer = new Qcow2Writer();
      writer.SetDiskImage(guest);
      writer.SetExternalDataFile("ours.img", raw);
      using (var image = File.Create(Path.Combine(dir, "ours.qcow2")))
      using (var data = File.Create(Path.Combine(dir, "ours.img")))
        writer.WriteTo(image, data);
      var w = FsInteropToolbox.WinToWsl(dir);
      var (stdout, stderr, code) = FsInteropToolbox.RunWsl(
        $"cd {w} && qemu-img check ours.qcow2 && qemu-img compare -f qcow2 -F raw ours.qcow2 guest.raw && " +
        $"qemu-img info ours.qcow2 | grep -E 'data file: ours.img' && qemu-img info ours.qcow2 | grep -E 'data file raw: {(raw ? "true" : "false")}'");
      Assert.That(code, Is.Zero, stdout + stderr);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  /// <summary>After our in-place edits QEMU must still find the image consistent and read the edited guest.</summary>
  [TestCase("datafile-raw.qcow2", "datafile-raw.img")]
  [TestCase("datafile.qcow2", "datafile.img")]
  [Category("ExternalInterop")]
  public void Given_AQemuImageWithADataFile_When_WeEditItInPlace_Then_QemuImgChecksAndComparesIt(string image, string data) {
    RequireQemu();
    var dir = TempDir();
    try {
      Place(dir, image, data);
      var path = Path.Combine(dir, image);
      File.WriteAllBytes(Path.Combine(dir, "expected.raw"), EditInPlace(path, ReadGuest(path)));
      var w = FsInteropToolbox.WinToWsl(dir);
      var (stdout, stderr, code) = FsInteropToolbox.RunWsl(
        $"cd {w} && qemu-img check {image} && qemu-img compare -f qcow2 -F raw {image} expected.raw" +
        (image == "datafile-raw.qcow2" ? $" && cmp {data} expected.raw" : ""));
      Assert.That(code, Is.Zero, stdout + stderr);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  /// <summary>
  /// Rebuilds the data-file vectors with qemu-img / qemu-io and checks they still decode to the
  /// committed hashes; with <c>CWB_QCOW2_WRITE_VECTORS=1</c> it writes them.
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_QemuTools_When_TheDataFileVectorsAreRegenerated_Then_TheyMatchTheCommittedOnes() {
    RequireQemu();
    var dir = TempDir();
    try {
      var w = FsInteropToolbox.WinToWsl(dir);
      var script = string.Join(" && ", [
        $"cd {w}",
        "qemu-img create -q -f qcow2 -o data_file=datafile-raw.img,data_file_raw=on,cluster_size=4096 datafile-raw.qcow2 256K",
        "qemu-io -f qcow2 -c 'write -P 0x11 0 10000' -c 'write -P 0x22 65536 4096' -c 'write -P 0x33 131072 70000' datafile-raw.qcow2",
        "qemu-img create -q -f qcow2 -o data_file=datafile.img,cluster_size=4096 datafile.qcow2 256K",
        "qemu-io -f qcow2 -c 'write -P 0x44 0 8192' -c 'write -P 0x55 16384 4096' -c 'write -z 16384 4096' -c 'write -P 0x66 100000 30000' datafile.qcow2",
        "for f in datafile-raw datafile; do qemu-img check -q $f.qcow2 && qemu-img convert -O raw $f.qcow2 $f.out; done",
        "for f in datafile-raw datafile; do printf '%s  %s.qcow2\\n' $(sha256sum < $f.out | cut -c1-64) $f; done > qemu-datafile.sha256",
      ]);
      var (stdout, stderr, code) = FsInteropToolbox.RunWsl(script);
      Assert.That(code, Is.Zero, stdout + stderr);

      var regenerated = File.ReadAllText(Path.Combine(dir, "qemu-datafile.sha256"));
      if (Environment.GetEnvironmentVariable("CWB_QCOW2_WRITE_VECTORS") == "1") {
        var vectors = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Qcow2", "ReferenceVectors");
        foreach (var name in new[] { "datafile-raw.qcow2", "datafile-raw.img", "datafile.qcow2", "datafile.img", "qemu-datafile.sha256" })
          File.Copy(Path.Combine(dir, name), Path.Combine(vectors, name), overwrite: true);
        Assert.Pass("Vectors written; rebuild to embed them.");
      }

      Assert.That(regenerated.Replace("\r\n", "\n"),
        Is.EqualTo(System.Text.Encoding.UTF8.GetString(Resource("qemu-datafile.sha256")).Replace("\r\n", "\n")));
      var manifest = regenerated.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split("  ")).ToDictionary(f => f[1], f => f[0]);
      foreach (var (image, _, _) in Vectors)
        Assert.That(Sha256(ReadGuest(Path.Combine(dir, image))), Is.EqualTo(manifest[image]), image);
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
