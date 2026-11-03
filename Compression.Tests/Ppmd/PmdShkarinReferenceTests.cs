using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Ppmd;

namespace Compression.Tests.Ppmd;

/// <summary>
/// The <c>.pmd</c> format against Dmitry Shkarin's PPMd programs, which define it. Files his var.H
/// and var.I compressors wrote decode byte-exact, and 7-Zip extracts what we write (see
/// ReferenceVectors/README.md).
/// </summary>
[TestFixture]
public sealed class PmdShkarinReferenceTests {

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PmdVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource PmdVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static byte[] Decode(byte[] file) {
    using var output = new MemoryStream();
    PpmdStream.Decompress(new MemoryStream(file), output);
    return output.ToArray();
  }

  private static byte[] Encode(byte[] data, int order = PpmdStream.DefaultOrder) {
    using var output = new MemoryStream();
    PpmdStream.Compress(new MemoryStream(data), output, order);
    return output.ToArray();
  }

  public static IEnumerable<TestCaseData> ShkarinFiles() {
    var manifest = System.Text.Encoding.ASCII.GetString(Resource("manifest.tsv"));
    foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var fields = line.Split('\t');
      yield return new TestCaseData(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2])
        .SetName($"GivenShkarinsPmd_{fields[0]}_WhenDecoded_ThenItIsTheInput");
    }
  }

  [TestCaseSource(nameof(ShkarinFiles)), Category("HappyPath")]
  public void ShkarinFileDecodes(string file, int length, string sha256) {
    var decoded = Decode(Resource(file));
    Assert.Multiple(() => {
      Assert.That(decoded, Has.Length.EqualTo(length));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(decoded)), Is.EqualTo(sha256));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenTheFreezeRestorationMethod_WhenDecoded_ThenItIsRefused()
    => Assert.That(() => Decode(Resource("shkarin-i-freeze.pmd")), Throws.TypeOf<InvalidDataException>().With.Message.Contains("freeze"));

  [Test, Category("ExceptionalCase")]
  public void GivenAnotherVariant_WhenDecoded_ThenItIsRefused() {
    var file = Resource("shkarin-i-one.pmd");
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), (ushort)((BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8)) & 0x0FFF) | (('G' - 'A') << 12)));
    Assert.That(() => Decode(file), Throws.TypeOf<InvalidDataException>().With.Message.Contains("variant G"));
  }

  [TestCase("shkarin-h-text.pmd", TestName = "GivenATruncatedVarHFile_WhenDecoded_ThenItIsRefused")]
  [TestCase("shkarin-i-text-r1.pmd", TestName = "GivenATruncatedVarIFile_WhenDecoded_ThenItIsRefused")]
  [Category("ExceptionalCase")]
  public void Truncated(string name) {
    var file = Resource(name);
    Assert.Throws<InvalidDataException>(() => Decode(file[..(file.Length / 2)]));
  }

  [TestCase(new byte[0], TestName = "GivenNoBytes_WhenDecoded_ThenItIsRefused")]
  [TestCase(new byte[] { 0x8F, 0xAF, 0xAC, 0x84, 0, 0 }, TestName = "GivenAShortHeader_WhenDecoded_ThenItIsRefused")]
  [TestCase(new byte[] { 0x8F, 0xAF, 0xAC, 0x85, 0, 0, 0, 0, 0xF5, 0x80, 0, 0, 0, 0, 0, 0 }, TestName = "GivenTheWrongSignature_WhenDecoded_ThenItIsRefused")]
  [Category("ExceptionalCase")]
  public void Malformed(byte[] file) => Assert.Throws<InvalidDataException>(() => Decode(file));

  [Test, Category("HappyPath")]
  public void GivenData_WhenEncoded_ThenTheHeaderIsShkarinsVarIHeader() {
    var file = Encode("hello"u8.ToArray(), order: 8);
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(file), Is.EqualTo(PpmdStream.Signature));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8)), Is.EqualTo((ushort)(7 | (15 << 4) | (8 << 12))));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(10)), Is.EqualTo((ushort)4));
      Assert.That(file.AsSpan(16, 4).ToArray(), Is.EqualTo("data"u8.ToArray()));
      Assert.That(Decode(file), Is.EqualTo("hello"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenShkarinsInput_WhenEncoded_ThenTheFileIsHisButForTheTimeStamp() {
    var shkarin = Resource("shkarin-i-data-o6.pmd");
    var ours = Encode(Decode(shkarin), order: 6);
    Assert.Multiple(() => {
      Assert.That(ours[..12], Is.EqualTo(shkarin[..12]));
      Assert.That(ours[16..], Is.EqualTo(shkarin[16..]));
    });
  }

  [TestCase(0, 6, TestName = "GivenNoBytes_WhenOurPmdIsExtractedBySevenZip_ThenItIsEmpty")]
  [TestCase(1, 2, TestName = "GivenOneByte_WhenOurPmdIsExtractedBySevenZip_ThenItIsTheInput")]
  [TestCase(300_000, 16, TestName = "GivenTextAtTheHighestOrder_WhenOurPmdIsExtractedBySevenZip_ThenItIsTheInput")]
  [TestCase(100_000, 6, TestName = "GivenText_WhenOurPmdIsExtractedBySevenZip_ThenItIsTheInput")]
  [Category("ArchiveExternal"), Category("EndToEnd")]
  public void SevenZipExtractsOurPmd(int length, int order) {
    if (!FsInteropToolbox.SevenZipAvailable) Assert.Ignore("7-Zip not available.");
    var words = "prediction by partial matching keeps the longest context it has seen "u8.ToArray();
    var random = new Random(length);
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = random.Next(12) == 0 ? (byte)random.Next(256) : words[(i * 5 + i / 17) % words.Length];

    var work = Path.Combine(Path.GetTempPath(), $"cwb_pmd_{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try {
      var path = Path.Combine(work, "ours.pmd");
      File.WriteAllBytes(path, Encode(data, order));
      var (stdout, stderr, exit) = FsInteropToolbox.Run7z($"x -y -o\"{Path.Combine(work, "out")}\" \"{path}\"");
      Assert.That(exit, Is.Zero, stdout + stderr);
      Assert.That(File.ReadAllBytes(Path.Combine(work, "out", "data")), Is.EqualTo(data));
    } finally {
      Directory.Delete(work, true);
    }
  }
}
