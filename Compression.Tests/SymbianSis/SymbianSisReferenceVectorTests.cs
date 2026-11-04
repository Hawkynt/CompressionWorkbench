using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Core.Checksums;
using Compression.Lib;
using Compression.Registry;
using FileFormat.SymbianSis;

namespace Compression.Tests.SymbianSis;

/// <summary>
/// Packages the Symbian SDK's makesis / signsis made (see ReferenceVectors/README.md), checked
/// against the input files they were built from (oracle.tsv).
/// </summary>
[TestFixture]
public class SymbianSisReferenceVectorTests {

  private static byte[] Load(string name) {
    using var resource = typeof(SymbianSisReferenceVectorTests).Assembly.GetManifestResourceStream("SymbianSisVectors." + name)!;
    using var ms = new MemoryStream();
    resource.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, (long Size, string Sha)> Oracle(string package) {
    using var resource = typeof(SymbianSisReferenceVectorTests).Assembly.GetManifestResourceStream("SymbianSisVectors.oracle.tsv")!;
    using var reader = new StreamReader(resource);
    var result = new Dictionary<string, (long, string)>();
    while (reader.ReadLine() is { } line)
      if (line.Length > 0 && !line.StartsWith('#') && line.Split('\t') is var f && f[0] == package)
        result[f[1]] = (long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), f[3]);
    return result;
  }

  private static Dictionary<string, string> ExtractAll(byte[] package) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-sis-" + Guid.NewGuid().ToString("N"));
    try {
      new SymbianSisFormatDescriptor().Extract(new MemoryStream(package), dir, null, null);
      return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
    }
  }

  [TestCase("hello.sis")]
  [TestCase("hello.sisx")]
  [TestCase("gui.sis")]
  public void GivenMakesisPackage_WhenListed_ThenTargetsAndSizesMatchItsInputs(string package) {
    var oracle = Oracle(package);
    var entries = new SymbianSisFormatDescriptor().List(new MemoryStream(Load(package)), null);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(oracle.Keys));
    foreach (var e in entries)
      Assert.That(e.OriginalSize, Is.EqualTo(oracle[e.Name].Size), e.Name);
  }

  [TestCase("hello.sis")]
  [TestCase("hello.sisx")]
  [TestCase("gui.sis")]
  public void GivenMakesisPackage_WhenExtracted_ThenEveryFileIsTheInputMakesisPacked(string package) {
    var oracle = Oracle(package);
    var written = ExtractAll(Load(package));
    Assert.That(written.Keys, Is.EquivalentTo(oracle.Keys));
    foreach (var (name, sha) in written)
      Assert.That(sha, Is.EqualTo(oracle[name].Sha), name);
  }

  [Test]
  public void GivenPackageWithADeflatedResource_WhenExtracted_ThenBothFilesPassTheirSha1() {
    var reader = new SymbianSisReader(new MemoryStream(Load("hello_reg.sis")));
    Assert.That(reader.Entries.Select(e => e.FileName), Is.EqualTo(new[] { "sys/bin/hello.exe", "private/10003a3f/import/apps/hello_reg.rsc" }));
    foreach (var e in reader.Entries)
      Assert.That(reader.Extract(e), Has.Length.EqualTo(e.Size), e.FileName);
    Assert.That(reader.Entries[1].CompressedSize, Is.LessThan(reader.Entries[1].Size));
  }

  [Test]
  public void GivenPackages_WhenOpened_ThenTheControllerInfoIsRead() {
    var unsigned = new SymbianSisReader(new MemoryStream(Load("hello.sis")));
    var signed = new SymbianSisReader(new MemoryStream(Load("hello.sisx")));
    var gui = new SymbianSisReader(new MemoryStream(Load("gui.sis")));
    Assert.Multiple(() => {
      Assert.That(unsigned.PackageUid, Is.EqualTo(0xE79E4CF9u));
      Assert.That(unsigned.Names, Is.EqualTo(new[] { "hello" }));
      Assert.That(unsigned.VendorUniqueName, Is.EqualTo("Vendor"));
      Assert.That(unsigned.Version, Is.EqualTo((1, 0, 24)));
      Assert.That(unsigned.InstallType, Is.Zero);
      Assert.That(unsigned.SignatureChains, Is.Zero);
      Assert.That(signed.SignatureChains, Is.EqualTo(1));
      Assert.That(gui.PackageUid, Is.EqualTo(0xE5D1A001u));
      Assert.That(gui.Version, Is.EqualTo((1, 0, 0)));
      Assert.That(gui.EmbeddedPackages, Is.Zero);
    });
  }

  [Test]
  public void GivenPackageOnDisk_WhenDetected_ThenSymbianSisIsChosen() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-sis-" + Guid.NewGuid().ToString("N") + ".sisx");
    try {
      File.WriteAllBytes(path, Load("hello.sisx"));
      Assert.That(FormatDetector.Detect(path), Is.EqualTo(FormatDetector.Format.SymbianSis));
    } finally {
      File.Delete(path);
    }
  }

  // The controller of gui.sis is the SISCompressed field after the header (16), the SISContents
  // header (8) and the two 12-byte checksum fields.
  private const int ControllerField = 16 + 8 + 12 + 12;

  [Test]
  public void GivenDamagedController_WhenListed_ThenTheControllerChecksumRefusesItAsPartial() {
    var data = Load("gui.sis");
    data[ControllerField + 40] ^= 0x01;
    var entries = new SymbianSisFormatDescriptor().List(new MemoryStream(data), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [Test]
  public void GivenDamagedData_WhenOpened_ThenTheDataChecksumRefusesIt() {
    var data = Load("gui.sis");
    data[^100] ^= 0x01;
    Assert.That(() => new SymbianSisReader(new MemoryStream(data)), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("SISDataChecksum"));
  }

  [Test]
  public void GivenDamagedFileWithRepairedDataChecksum_WhenExtracted_ThenItsSha1RefusesIt() {
    var data = Load("gui.sis");
    // gui.exe is stored, so flipping a byte inside it keeps the structure intact.
    var controllerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ControllerField + 4));
    var dataField = ControllerField + 8 + controllerLength + (4 - controllerLength % 4) % 4;
    var dataLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dataField + 4));
    data[dataField + 200] ^= 0x01;
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16 + 8 + 12 + 8), Crc16Ccitt.Compute(data.AsSpan(dataField, 8 + dataLength)));
    var reader = new SymbianSisReader(new MemoryStream(data));
    var exe = reader.Entries.Single(e => e.FileName == "sys/bin/gui.exe");
    Assert.That(() => reader.Extract(exe), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("SHA-1"));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "resource/apps/gui.rsc")), Has.Length.EqualTo(109));
  }

  [TestCase(23)]
  [TestCase(100)]
  [TestCase(2000)]
  public void GivenTruncatedPackage_WhenListed_ThenAPartialMetadataEntryIsReturned(int length) {
    var entries = new SymbianSisFormatDescriptor().List(new MemoryStream(Load("gui.sis")[..length]), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [Test]
  public void GivenNonSisInputs_WhenProbed_ThenTheyAreNotSis() {
    var epoc = new byte[32];
    BinaryPrimitives.WriteUInt32LittleEndian(epoc.AsSpan(4), 0x10000419); // pre-9 EPOC SIS: UID 2
    Assert.Multiple(() => {
      Assert.That(SymbianSisReader.IsSis(new MemoryStream(new byte[64])), Is.False);
      Assert.That(SymbianSisReader.IsSis(new MemoryStream(epoc)), Is.False);
      Assert.That(SymbianSisReader.IsSis(new MemoryStream(Load("gui.sis")[..15])), Is.False);
      Assert.That(SymbianSisReader.IsSis(new MemoryStream(Load("gui.sis"))), Is.True);
    });
  }

  [TestCase(@"!:\sys\bin\app.exe", "sys/bin/app.exe")]
  [TestCase(@"c:\data\notes.txt", "data/notes.txt")]
  [TestCase(@"e:\..\escape\x", "escape/x")]
  [TestCase(@"!:\", "notarget/file")]
  [TestCase(@"plain", "plain")]
  public void GivenTarget_WhenNamed_ThenTheDriveAndDotSegmentsAreDropped(string target, string expected)
    => Assert.That(SymbianSisReader.TargetToName(target), Is.EqualTo(expected));

  private static byte[] Rewrite(byte[] original) {
    var reader = new SymbianSisReader(new MemoryStream(original));
    return SymbianSisWriter.Write(new() {
      Name = reader.Names.Single(),
      Uid = reader.PackageUid,
      Vendor = reader.VendorUniqueName,
      LocalizedVendor = reader.VendorNames.Single(),
      Version = reader.Version,
      CreationTime = reader.CreationTime!.Value,
      Files = reader.Entries.Select(e => new SymbianSisFile(e.Target, reader.Extract(e))).ToList(),
    });
  }

  /// <summary>The UID header, the data checksum, the inflated controller and the SISData field of a package.</summary>
  private static (byte[] Uids, byte[] DataChecksum, byte[] Controller, byte[] Data) Parts(byte[] package) {
    var controllerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(ControllerField + 4));
    var dataField = ControllerField + 8 + controllerLength + (4 - controllerLength % 4) % 4;
    return (
      package[..16],
      package[(16 + 8 + 12)..(16 + 8 + 24)],
      FileFormat.Zlib.ZlibStream.Decompress(package.AsSpan(ControllerField + 8 + 12, controllerLength - 12)),
      package[dataField..]);
  }

  // The packages' .pkg lines and files, packed again. Everything makesis decides — every field, its
  // order, which files to deflate, the file data, the checksums of the data — must come back
  // exactly; only the deflate bitstream of the controller may differ, because our encoder is not
  // zlib's (it decodes to the identical controller).
  [TestCase("hello.sis")]
  [TestCase("hello_reg.sis")]
  [TestCase("gui.sis")]
  public void GivenTheLinesOfAMakesisPackage_WhenWritten_ThenEverythingMakesisDecidesComesBack(string package) {
    var original = Load(package);
    var (uids, dataCrc, controller, data) = Parts(original);
    var rewritten = Parts(Rewrite(original));
    Assert.Multiple(() => {
      Assert.That(rewritten.Uids, Is.EqualTo(uids), "UID header and checked UID");
      Assert.That(Convert.ToHexString(rewritten.Controller), Is.EqualTo(Convert.ToHexString(controller)), "SISController");
      Assert.That(Convert.ToHexString(rewritten.Data), Is.EqualTo(Convert.ToHexString(data)), "SISData, with makesis's deflated files");
      Assert.That(rewritten.DataChecksum, Is.EqualTo(dataCrc), "SISDataChecksum");
    });
  }

  [Test]
  public void GivenTheLinesOfGuiSis_WhenWritten_ThenTheWholeFileIsMakesisOwn()
    => Assert.That(Convert.ToHexString(Rewrite(Load("gui.sis"))), Is.EqualTo(Convert.ToHexString(Load("gui.sis"))));

  [Test]
  public void GivenMakesisPackages_WhenOpened_ThenTheCreationTimeCountsMonthsFromZero() {
    // gui.sis stores 2026, month byte 8, day 19, 16:01:18.
    var gui = new SymbianSisReader(new MemoryStream(Load("gui.sis")));
    Assert.That(gui.CreationTime, Is.EqualTo(new DateTime(2026, 9, 19, 16, 1, 18, DateTimeKind.Utc)));
    Assert.That(gui.VendorNames, Is.EqualTo(new[] { "Vendor-EN" }));
  }

  [Test]
  public void GivenFilesOfEveryShape_WhenCreatedAndReadBack_ThenEveryFileReturnsUnchanged() {
    var rnd = new Random(7);
    var noise = new byte[100_000];
    rnd.NextBytes(noise);
    var inputs = new (string Name, byte[] Data)[] {
      ("empty.txt", []),
      ("one.bin", [0x42]),
      ("text/readme.txt", System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible line\n", 500)))),
      ("deep/a/b/c/noise.bin", noise),
      ("unicode/ä ö ü.txt", "Grüße"u8.ToArray()),
    };
    var descriptor = new SymbianSisFormatDescriptor();
    using var ms = new MemoryStream();
    descriptor.Create(ms, inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data)).Prepend(new ArchiveInputInfo("deep", "deep", IsDirectory: true)).ToList(), new FormatCreateOptions());
    var package = ms.ToArray();
    var reader = new SymbianSisReader(new MemoryStream(package));
    Assert.That(reader.Entries.Select(e => e.FileName), Is.EqualTo(inputs.Select(i => i.Name)));
    foreach (var (name, data) in inputs)
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == name)), Is.EqualTo(data), name);
    Assert.Multiple(() => {
      Assert.That(reader.PackageUid, Is.EqualTo(SymbianSisFormatDescriptor.CreatedPackageUid));
      Assert.That(reader.Entries.Single(e => e.FileName == "text/readme.txt").CompressedSize, Is.LessThan(9000), "deflated");
      Assert.That(reader.Entries.Single(e => e.FileName == "deep/a/b/c/noise.bin").CompressedSize, Is.EqualTo(noise.Length), "stored");
      Assert.That(SymbianSisReader.IsSis(new MemoryStream(package)), Is.True);
    });
  }

  [Test]
  public void GivenE32Images_WhenTheirCapabilitiesAreRead_ThenOnlyAnEpocHeaderCounts() {
    var image = new byte[0x8C];
    "EPOC"u8.CopyTo(image.AsSpan(0x10));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x88), 0x000BE000);
    Assert.Multiple(() => {
      Assert.That(SymbianSisWriter.E32Capabilities(image), Is.EqualTo(0x000BE000u));
      Assert.That(SymbianSisWriter.E32Capabilities(image.AsSpan(0, 0x8B)), Is.Null, "too short");
      image[0x10] = (byte)'X';
      Assert.That(SymbianSisWriter.E32Capabilities(image), Is.Null, "no EPOC signature");
    });
  }
}
