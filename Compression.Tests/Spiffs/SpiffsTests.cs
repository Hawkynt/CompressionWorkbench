#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Spiffs;

namespace Compression.Tests.Spiffs;

/// <summary>
/// The reader against images mkspiffs (three build configurations) and ESP-IDF's
/// spiffsgen.py wrote, the writer by round trip and boundary cases. Provenance in
/// <c>ReferenceVectors/README.md</c>.
/// </summary>
[TestFixture]
public sealed class SpiffsTests {

  private static byte[] Vector(string name) {
    using var resource = typeof(SpiffsTests).Assembly.GetManifestResourceStream("SpiffsVectors." + name)
      ?? throw new FileNotFoundException($"embedded reference vector {name} is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gzip.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, string> Manifest() {
    using var resource = typeof(SpiffsTests).Assembly.GetManifestResourceStream("SpiffsVectors.manifest.txt")!;
    using var text = new StreamReader(resource);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    while (text.ReadLine() is { } line)
      if (line.Length > 65) result[line[65..]] = line[..64];
    return result;
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCase("mkspiffs-esp8266.spiffs.gz", 256, 8192, 0, true, false)]
  [TestCase("mkspiffs-generic.spiffs.gz", 256, 4096, 0, false, true)]
  [TestCase("mkspiffs-esp32.spiffs.gz", 256, 4096, 4, false, true)]
  [TestCase("spiffsgen.spiffs.gz", 256, 4096, 4, false, true)]
  [Category("ThirdParty")]
  public void GivenAToolMadeImage_WhenRead_ThenTheGeometryIsFoundAndEveryFileMatches(
      string vector, int page, int block, int meta, bool aligned, bool length) {
    var r = new SpiffsReader(Vector(vector));
    Assert.That(r.Geometry, Is.EqualTo(new SpiffsGeometry(page, block, meta, aligned, length)));
    var manifest = Manifest();
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(manifest.Keys));
    foreach (var e in r.Entries)
      Assert.That(Sha(r.Extract(e)), Is.EqualTo(manifest[e.Name]), e.Name);
  }

  [TestCase("ESP-IDF / Arduino ESP32")]
  [TestCase("Arduino ESP8266")]
  [TestCase("mkspiffs generic")]
  public void GivenEveryProfile_WhenWrittenAndRead_ThenTheFilesRoundTrip(string profile) {
    var files = new Dictionary<string, byte[]> {
      ["empty"] = [],
      ["a/b/one-page"] = Pattern(251, 1),
      ["a/two-pages"] = Pattern(252, 2),
      ["index-pages.bin"] = Pattern(60_000, 3),
      [new string('n', 30)] = [7],
    };
    var image = Create(profile, files);
    var r = new SpiffsReader(image);
    Assert.That(r.Geometry, Is.EqualTo(SpiffsFormatDescriptor.Profiles[profile]));
    Assert.That(r.Entries.ToDictionary(e => e.Name, e => r.Extract(e)), Is.EquivalentTo(files));
  }

  [Test]
  public void GivenANameOfThirtyTwoBytesWithItsSlash_WhenOffered_ThenItIsRefused() {
    var d = new SpiffsFormatDescriptor();
    Assert.That(d.CanAccept(ArchiveInputInfo.InMemory(new string('n', 30), new byte[] { 1 }), out _), Is.True);
    Assert.That(d.CanAccept(ArchiveInputInfo.InMemory(new string('n', 31), new byte[] { 1 }), out var why), Is.False);
    Assert.That(why, Does.Contain("31"));
  }

  [Test]
  public void GivenFilesThatDoNotFit_WhenWritten_ThenTheWriterRefuses() {
    var w = new SpiffsWriter(SpiffsFormatDescriptor.Profiles["mkspiffs generic"], 3 * 4096);
    w.AddFile("big", new byte[20_000]);
    Assert.Throws<IOException>(() => w.Build());
  }

  [Test]
  public void GivenAnImageWithoutMagic_WhenRead_ThenItIsRejected() {
    var image = new byte[4 * 4096];
    image.AsSpan().Fill(0xFF);
    Assert.Throws<InvalidDataException>(() => _ = new SpiffsReader(image));
  }

  [Test]
  public void GivenAnImageCutMidBlock_WhenRead_ThenItIsRejected() {
    var image = Vector("mkspiffs-generic.spiffs.gz");
    Assert.Throws<InvalidDataException>(() => _ = new SpiffsReader(image.AsSpan(0, image.Length - 100).ToArray()));
  }

  [Test]
  public void GivenADataPageMarkedDeleted_WhenExtracted_ThenTheFileIsRefusedNotInvented() {
    var image = Create("mkspiffs generic", new Dictionary<string, byte[]> { ["f"] = Pattern(1000, 5) });
    var g = SpiffsFormatDescriptor.Profiles["mkspiffs generic"];
    // The writer places the header first, then the data pages: clear DELET on the second data page.
    var page = g.LookupPages + 2;
    image[page * g.PageSize + 4] &= unchecked((byte)~SpiffsGeometry.FlagDelete);
    var r = new SpiffsReader(image, g);
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries.Single()));
  }

  [Test]
  public void GivenTheDescriptor_WhenCreatingAndListing_ThenTheEntriesComeBack() {
    var d = new SpiffsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("web/index.html", "<p/>"u8.ToArray())], new FormatCreateOptions());
    ms.Position = 0;
    Assert.That(d.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "web/index.html" }));
  }

  internal static byte[] Pattern(int size, int seed) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i) data[i] = (byte)(i * 13 + seed + (i >> 8));
    return data;
  }

  internal static byte[] Create(string profile, IReadOnlyDictionary<string, byte[]> files) {
    var d = new SpiffsFormatDescriptor();
    using var ms = new MemoryStream();
    var options = new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Profile"] = profile } };
    d.Create(ms, files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(), options);
    return ms.ToArray();
  }
}
