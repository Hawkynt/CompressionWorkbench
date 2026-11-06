using System.Reflection;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// 7zFormat.txt lets the folder list, the names, the times and the attributes of a header live in
/// a separate stream (an <c>External</c> byte of 1 plus a data index into
/// <c>kAdditionalStreamsInfo</c>). The reader refuses such a header with
/// <see cref="NotSupportedException"/> and records additional streams as uncarried metadata. These
/// tests record that the captured conformance vectors and five exercised 7-Zip header profiles,
/// including headers with 20,000 names, do not need external header data.
/// </summary>
[TestFixture]
public sealed class SevenZipExternalReferenceTests {
  private static IEnumerable<string> Vectors()
    => Assembly.GetExecutingAssembly().GetManifestResourceNames()
      .Where(static n => n.StartsWith("SevenZipConformanceVectors.", StringComparison.Ordinal) && n.EndsWith(".7z", StringComparison.Ordinal));

  private static void AssertNoExternalReferences(Stream archive, string? password, string context) {
    using var reader = new SevenZipReader(archive, password: password);
    Assert.That(reader.UncarriedMetadata, Does.Not.Contain("additional streams"), $"{context}: header data in an additional stream");
  }

  [TestCaseSource(nameof(Vectors))]
  [Category("ThirdPartyProof")]
  public void Given_A7ZipVector_When_ItsHeaderIsRead_Then_NothingLivesInAnExternalStream(string resource) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
    AssertNoExternalReferences(stream, resource.Contains(".aes", StringComparison.Ordinal) ? "S3cret!" : null, resource);
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AHeaderWhoseNamesLiveInAnExternalStream_When_Read_Then_ItIsRefusedAsUnsupported() {
    using var built = new MemoryStream();
    using (var writer = new SevenZipWriter(built, SevenZipCodec.Copy, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "a.txt" }, "a"u8);
      writer.Finish();
    }
    var bytes = built.ToArray();
    var headerStart = 32 + (int)BitConverter.ToInt64(bytes, 12);
    var headerSize = (int)BitConverter.ToInt64(bytes, 20);
    // kName (0x11), its size, then the External byte: set it, as a header pointing into
    // additional streams would.
    var name = bytes.AsSpan(headerStart, headerSize).IndexOf((byte)0x11);
    bytes[headerStart + name + 2] = 1;
    BitConverter.TryWriteBytes(bytes.AsSpan(28), Compression.Core.Checksums.Crc32.Compute(bytes.AsSpan(headerStart, headerSize)));
    BitConverter.TryWriteBytes(bytes.AsSpan(8), Compression.Core.Checksums.Crc32.Compute(bytes.AsSpan(12, 20)));

    Assert.That(() => new SevenZipReader(new MemoryStream(bytes)), Throws.TypeOf<NotSupportedException>().With.Message.Contains("additional stream"));
  }

  [TestCase("plain")]
  [TestCase("packed", "-mhc=on")]
  [TestCase("all-times", "-mhc=off", "-mtc=on", "-mta=on")]
  [TestCase("encrypted", "-pS3cret!", "-mhe=on")]
  [TestCase("non-solid", "-mhc=off", "-ms=off")]
  [Category("EndToEnd")]
  public void Given_7ZipWritingAHeaderWithManyNames_When_Read_Then_NothingLivesInAnExternalStream(string name, params string[] switches) {
    SevenZipOracle.Require();
    var root = Path.Combine(Path.GetTempPath(), $"cwb_7zext_{Guid.NewGuid():N}");
    try {
      var source = Path.Combine(root, "src");
      var tree = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal);
      for (var d = 0; d < 40; ++d)
        for (var f = 0; f < 500; ++f)
          tree[$"dir{d:D2}/a-rather-long-file-name-number-{f:D4}.txt"] = f % 7 == 0 ? [] : [(byte)d, (byte)f];
      SevenZipOracle.Materialize(source, tree);
      var archive = Path.Combine(root, $"{name}.7z");
      SevenZipOracle.RunOk(source, ["a", "-y", "-r", archive, .. switches, "*"]);

      using var stream = File.OpenRead(archive);
      AssertNoExternalReferences(stream, switches.Any(static s => s.StartsWith("-p", StringComparison.Ordinal)) ? "S3cret!" : null, name);
    } finally {
      try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
  }
}
