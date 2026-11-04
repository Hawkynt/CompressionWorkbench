using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Ewf;

namespace Compression.Tests.Ewf;

/// <summary>
/// EWF2-Ex01: the images libewf's ewfacquire wrote (Ex01Vectors/README.md) read back to the media
/// they were acquired from, segment sets included; our writer's images pass libewf's ewfverify and
/// ewfexport; damage and unsupported variants are refused.
/// </summary>
[TestFixture]
public sealed class Ex01Tests {
  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Ex01Vectors." + name)
      ?? throw new InvalidOperationException($"Missing embedded vector {name}.");
    using var copy = new MemoryStream();
    if (name.EndsWith(".gz", StringComparison.Ordinal)) {
      using var gzip = new GZipStream(stream, CompressionMode.Decompress);
      gzip.CopyTo(copy);
    } else
      stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static Dictionary<string, string> Sources()
    => System.Text.Encoding.UTF8.GetString(Resource("sources.sha256"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split("  "))
      .ToDictionary(f => f[1], f => f[0]);

  private static byte[] ReadMedia(Stream image) {
    using var media = new Ewf2MediaStream(Ewf2Image.Open(image));
    using var copy = new MemoryStream();
    media.CopyTo(copy);
    return copy.ToArray();
  }

  private static byte[] Medium(int size, int seed) {
    var medium = new byte[size];
    var text = "our ex01 medium "u8;
    for (var i = 0; i < size / 2; ++i)
      medium[i] = text[i % text.Length];
    new Random(seed).NextBytes(medium.AsSpan(size * 3 / 4));
    return medium;
  }

  [TestCase("deflate.Ex01")]
  [TestCase("stored.Ex01")]
  [Category("ThirdPartyProof")]
  public void Given_ALibewfEx01_When_Read_Then_TheAcquiredMediumReturns(string name) {
    var image = Resource(name + ".gz");
    Assert.That(Sha256(ReadMedia(new MemoryStream(image))), Is.EqualTo(Sources()[name]));
    using var opened = Ewf2Image.Open(new MemoryStream(image));
    Assert.That(opened.Md5, Is.Not.Null);
    Assert.That(opened.DeviceInformation["bp"], Is.EqualTo("512"));
  }

  [Test]
  [Category("ThirdPartyProof")]
  public void Given_ALibewfSegmentSet_When_OpenedFromItsFirstFile_Then_TheWholeMediumReturns() {
    var dir = Directory.CreateTempSubdirectory("cwb-ex01-").FullName;
    try {
      foreach (var part in new[] { "multi.Ex01", "multi.Ex02", "multi.Ex03" })
        File.WriteAllBytes(Path.Combine(dir, part), Resource(part + ".gz"));
      using (var file = File.OpenRead(Path.Combine(dir, "multi.Ex01")))
        Assert.That(Sha256(ReadMedia(file)), Is.EqualTo(Sources()["multi.Ex01"]));
      File.Delete(Path.Combine(dir, "multi.Ex03"));
      using var broken = File.OpenRead(Path.Combine(dir, "multi.Ex01"));
      Assert.Throws<InvalidDataException>(() => Ewf2Image.Open(broken));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_ASegmentOfASetInMemory_When_Opened_Then_ItAsksForAFile()
    => Assert.Throws<NotSupportedException>(() => Ewf2Image.Open(new MemoryStream(Resource("multi.Ex01.gz"))));

  [Test]
  public void Given_TheDescriptor_When_ListingAndExtracting_Then_MediaAndMetadataAppear() {
    var descriptor = new Ewf2FormatDescriptor();
    var image = Resource("deflate.Ex01.gz");
    var list = descriptor.List(new MemoryStream(image), null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "media.raw", "metadata.ini" }));
    Assert.That(list[0].OriginalSize, Is.EqualTo(192 * 1024));
    var media = descriptor.ExtractEntryToMemory(new MemoryStream(image), "media.raw", null);
    Assert.That(Sha256(media), Is.EqualTo(Sources()["deflate.Ex01"]));
    var metadata = System.Text.Encoding.UTF8.GetString(descriptor.ExtractEntryToMemory(new MemoryStream(image), "metadata.ini", null));
    Assert.That(metadata, Does.Contain("md5 = ").And.Contain("[case]"));
  }

  [Test]
  public void Given_AFlippedChunkByte_When_Extracted_Then_TheDamageIsReported() {
    var image = Resource("stored.Ex01.gz");
    image[1000] ^= 0x40; // inside the first stored chunk
    Assert.Throws<InvalidDataException>(() => ReadMedia(new MemoryStream(image)));
  }

  [Test]
  public void Given_AFlippedDescriptorByte_When_Opened_Then_ItIsRefused() {
    var image = Resource("deflate.Ex01.gz");
    image[^20] ^= 1;
    Assert.Throws<InvalidDataException>(() => Ewf2Image.Open(new MemoryStream(image)));
  }

  [Test]
  public void Given_AnEncryptedSection_When_Opened_Then_ItIsRefused() {
    var image = Resource("deflate.Ex01.gz");
    var at = image.Length - 64; // the done section's descriptor
    image[at + 4] |= 2;
    var adler = Compression.Core.Checksums.Adler32.Compute(image.AsSpan(at, 60));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 60), adler);
    Assert.Throws<NotSupportedException>(() => Ewf2Image.Open(new MemoryStream(image)));
  }

  [Test]
  public void Given_AnLx01_When_Opened_Then_ItIsRefusedByName() {
    var image = Resource("deflate.Ex01.gz");
    "LEF2"u8.CopyTo(image);
    Assert.That(() => Ewf2Image.Open(new MemoryStream(image)), Throws.TypeOf<NotSupportedException>().With.Message.Contains("Lx01"));
  }

  [TestCase(true, 300 * 1024)]
  [TestCase(false, 300 * 1024)]
  [TestCase(true, 1000)]
  [TestCase(true, 0)]
  public void Given_OurWriter_When_ReadBack_Then_TheMediumRoundTrips(bool compress, int size) {
    var medium = Medium(size, size);
    using var ms = new MemoryStream();
    new Ex01WriterAdapter(compress).Write(ms, medium);
    ms.Position = 0;
    var read = ReadMedia(ms);
    var padded = new byte[(size + 511) / 512 * 512];
    medium.CopyTo(padded, 0);
    Assert.That(read, Is.EqualTo(padded));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Given_MultipleFiles_When_Creating_Then_TheyAreRefusedBeforeWriting(bool namedMedia) {
    var descriptor = new Ewf2FormatDescriptor();
    using var output = new MemoryStream();
    var inputs = new[] {
      ArchiveInputInfo.InMemory(namedMedia ? "media.raw" : "first.bin", [1, 2]),
      ArchiveInputInfo.InMemory("second.bin", [3, 4]),
    };
    Assert.That(() => descriptor.Create(output, inputs, new FormatCreateOptions()), Throws.ArgumentException);
    Assert.That(output.Length, Is.Zero);
  }

  [Test]
  public void Given_ADirectory_When_Creating_Then_ItIsRefusedBeforeWriting() {
    var descriptor = new Ewf2FormatDescriptor();
    var directory = new ArchiveInputInfo("", "folder", true);
    Assert.That(descriptor.CanAccept(directory, out var reason), Is.False);
    Assert.That(reason, Is.Not.Empty);
    using var output = new MemoryStream();
    Assert.That(() => descriptor.Create(output, [directory], new FormatCreateOptions()), Throws.ArgumentException);
    Assert.That(output.Length, Is.Zero);
  }

  [Test]
  public void Given_ASingleImageWithAnyName_When_Created_Then_AllItsBytesAreExposedAsMedia() {
    var descriptor = new Ewf2FormatDescriptor();
    var media = Medium(1024, 1);
    var input = ArchiveInputInfo.InMemory("disk.img", media);
    Assert.That(descriptor.CanAccept(input, out _), Is.True);
    using var output = new MemoryStream();
    descriptor.Create(output, [input], new FormatCreateOptions());
    output.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(output, "media.raw", null), Is.EqualTo(media));
  }

  [Test]
  public void Given_TheDescriptor_When_Created_Then_ItListsTheMedium() {
    var descriptor = new Ewf2FormatDescriptor();
    using var ms = new MemoryStream();
    descriptor.Create(ms, [ArchiveInputInfo.InMemory("media.raw", Medium(65536, 1))], new FormatCreateOptions());
    ms.Position = 0;
    Assert.That(descriptor.List(ms, null)[0].OriginalSize, Is.EqualTo(65536));
    Assert.Throws<NotSupportedException>(() => descriptor.Create(new MemoryStream(), [], new FormatCreateOptions { Password = "x" }));
  }

  [TestCase(2, "multi.Ex02")]
  [TestCase(99, "multi.Ex99")]
  [TestCase(100, "multi.ExAA")]
  [TestCase(101, "multi.ExAB")]
  [TestCase(775, "multi.ExZZ")]
  [TestCase(776, "multi.EyAA")]
  public void Given_ASegmentNumber_When_Named_Then_TheEx01SchemeIsFollowed(int number, string expected)
    => Assert.That(Path.GetFileName(Ewf2Image.SegmentPath("multi.Ex01", number)), Is.EqualTo(expected));

  private sealed class Ex01WriterAdapter(bool compress) {
    public void Write(Stream output, byte[] medium) => new Ewf2Writer { Compress = compress }.Write(output, medium);
  }

  private static string LibewfTool(string tool) => $"$HOME/libewf-exp/bin/{tool}";

  /// <summary>libewf's ewfverify must accept our Ex01 and ewfexport must return the medium.</summary>
  [TestCase(true)]
  [TestCase(false)]
  [Category("ExternalInterop")]
  public void Given_OurEx01_When_LibewfVerifiesAndExportsIt_Then_ItAgrees(bool compress) {
    if (!FsInteropToolbox.WslAvailable || FsInteropToolbox.RunWsl($"test -x {LibewfTool("ewfverify")}").ExitCode != 0)
      Assert.Ignore("libewf-experimental-20240506 is needed at ~/libewf-exp in WSL (see Ex01Vectors/mkfixtures.sh).");
    var dir = Directory.CreateTempSubdirectory("cwb-ex01-").FullName;
    try {
      var medium = Medium(1 << 20, 9);
      using (var file = File.Create(Path.Combine(dir, "ours.Ex01")))
        new Ewf2Writer { Compress = compress }.Write(file, medium);
      var w = FsInteropToolbox.WinToWsl(dir);
      var (stdout, stderr, code) = FsInteropToolbox.RunWsl(
        $"cd {w} && {LibewfTool("ewfverify")} -q ours.Ex01 && {LibewfTool("ewfexport")} -u -q -f raw -t out ours.Ex01 >/dev/null && sha256sum out.raw");
      Assert.That(code, Is.Zero, stdout + stderr);
      Assert.That(stdout, Does.Contain("ewfverify: SUCCESS").And.Contain(Sha256(medium) + "  out.raw"));
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
