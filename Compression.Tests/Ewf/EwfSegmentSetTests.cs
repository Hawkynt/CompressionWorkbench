using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Ewf;

namespace Compression.Tests.Ewf;

/// <summary>
/// Multi-segment EWF-E01 sets written by libewf's <c>ewfacquire</c> (ewf-tools 20171104, Ubuntu
/// 24.04 in WSL), in the EnCase 6 layout (table base offsets) and the EnCase 5 layout (file-relative
/// offsets), split into 1 MiB segment files (.E01, .E02, .E03). Opened from the first file, the
/// reader must find the others next to it and return the acquired media byte for byte.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class EwfSegmentSetTests {

  /// <summary>The 2.5 MiB medium both sets acquired: compressible, so the stored segments gzip well.</summary>
  internal static byte[] Medium() {
    var medium = new byte[5 * 512 * 1024];
    var text = "ewf segment set test medium "u8;
    for (var i = 0; i < medium.Length; ++i)
      medium[i] = text[(i + i / 4096) % text.Length];
    return medium;
  }

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static string[] Resources(string set) => Assembly.GetExecutingAssembly().GetManifestResourceNames()
    .Where(n => n.StartsWith($"EwfVectors.{set}.", StringComparison.Ordinal)).Order().ToArray();

  /// <summary>Unpacks a committed set into a fresh directory; returns the path of its .E01.</summary>
  private static string Unpack(string set, string dir) {
    foreach (var resource in Resources(set)) {
      var name = resource["EwfVectors.".Length..^".gz".Length];
      using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
      using var gzip = new GZipStream(source, CompressionMode.Decompress);
      using var target = File.Create(Path.Combine(dir, name));
      gzip.CopyTo(target);
    }
    return Path.Combine(dir, set + ".E01");
  }

  [TestCase("encase6")]
  [TestCase("encase5")]
  public void Given_AnEwfacquireSegmentSet_When_OpenedFromItsFirstFile_Then_TheMediaIsTheAcquiredOne(string set) {
    var dir = Directory.CreateTempSubdirectory("cwb-ewf-").FullName;
    try {
      var first = Unpack(set, dir);
      Assert.That(Resources(set), Has.Length.GreaterThanOrEqualTo(3), "a set of several segment files");
      using var file = File.OpenRead(first);
      using var media = EwfMediaStream.Open(file);
      Assert.That(media.SegmentCount, Is.EqualTo(Resources(set).Length));
      var bytes = new byte[media.Length];
      media.ReadExactly(bytes);
      Assert.That(Sha256(bytes), Is.EqualTo(Sha256(Medium())));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_ASegmentSet_When_ListedAndExtractedThroughTheDescriptor_Then_MediaRawIsComplete() {
    var dir = Directory.CreateTempSubdirectory("cwb-ewf-").FullName;
    try {
      var first = Unpack("encase6", dir);
      var descriptor = new EwfFormatDescriptor();
      using var file = File.OpenRead(first);
      var media = descriptor.List(file, null).Single(e => e.Name == "media.raw");
      Assert.That(media.OriginalSize, Is.EqualTo(Medium().Length));
      var output = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
      file.Position = 0;
      descriptor.Extract(file, output, null, ["media.raw"]);
      Assert.That(Sha256(File.ReadAllBytes(Path.Combine(output, "media.raw"))), Is.EqualTo(Sha256(Medium())));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_AMissingMiddleSegment_When_Opened_Then_ItIsNamedAndRefused() {
    var dir = Directory.CreateTempSubdirectory("cwb-ewf-").FullName;
    try {
      var first = Unpack("encase6", dir);
      File.Delete(Path.Combine(dir, "encase6.E02"));
      using var file = File.OpenRead(first);
      var error = Assert.Throws<InvalidDataException>(() => EwfMediaStream.Open(file));
      Assert.That(error!.Message, Does.Contain("encase6.E02"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_AFirstSegmentInMemory_When_Opened_Then_TheMissingSegmentsAreReportedNotZeroFilled() {
    var dir = Directory.CreateTempSubdirectory("cwb-ewf-").FullName;
    try {
      var first = File.ReadAllBytes(Unpack("encase6", dir));
      Assert.Throws<NotSupportedException>(() => EwfMediaStream.Open(new MemoryStream(first)));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_ADamagedStoredChunkInALaterSegment_When_Read_Then_ItIsRefused() {
    var dir = Directory.CreateTempSubdirectory("cwb-ewf-").FullName;
    try {
      var first = Unpack("encase6", dir);
      var second = Path.Combine(dir, "encase6.E02");
      var bytes = File.ReadAllBytes(second);
      bytes[bytes.Length / 2] ^= 0x01;
      File.WriteAllBytes(second, bytes);
      using var file = File.OpenRead(first);
      using var media = EwfMediaStream.Open(file);
      Assert.Throws<InvalidDataException>(() => media.ReadExactly(new byte[media.Length]));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_ASegmentSet_When_Edited_Then_ItIsRefusedAndTheFirstFileIsUntouched() {
    var dir = Directory.CreateTempSubdirectory("cwb-ewf-").FullName;
    try {
      var original = File.ReadAllBytes(Unpack("encase6", dir));
      using var stream = new MemoryStream();
      stream.Write(original);
      var descriptor = new EwfFormatDescriptor();
      Assert.Throws<NotSupportedException>(() => descriptor.Remove(stream, ["media.raw"]));
      Assert.Throws<NotSupportedException>(() => descriptor.Purge(stream));
      Assert.That(stream.ToArray(), Is.EqualTo(original));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [TestCase("x.E01", 2, "x.E02")]
  [TestCase("x.E01", 99, "x.E99")]
  [TestCase("x.E01", 100, "x.EAA")]
  [TestCase("x.E01", 101, "x.EAB")]
  [TestCase("x.E01", 775, "x.EZZ")]
  [TestCase("x.E01", 776, "x.FAA")]
  [TestCase("x.e01", 100, "x.eaa")]
  public void Given_ASegmentNumber_When_Named_Then_ItFollowsEwfNaming(string first, int number, string expected)
    => Assert.That(Path.GetFileName(EwfMediaStream.SegmentPath(first, number)), Is.EqualTo(expected));

  /// <summary>
  /// Acquires the medium with <c>ewfacquire</c> in WSL in both layouts, reads the sets back, checks
  /// <c>ewfexport</c> agrees, and with <c>CWB_EWF_WRITE_VECTORS=1</c> stores them (gzipped).
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_Ewfacquire_When_TheSetsAreRegenerated_Then_WeAndEwfexportReadTheMedium() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("ewfacquire") || !FsInteropToolbox.WslHasTool("ewfexport"))
      Assert.Ignore("ewfacquire and ewfexport (ewf-tools) are needed in WSL.");
    var dir = Directory.CreateTempSubdirectory("cwb-ewf-gen-").FullName;
    try {
      File.WriteAllBytes(Path.Combine(dir, "medium.raw"), Medium());
      var w = FsInteropToolbox.WinToWsl(dir);
      foreach (var (set, format, compression) in new[] { ("encase6", "encase6", "none"), ("encase5", "encase5", "none"), ("deflate", "encase6", "fast") }) {
        var (stdout, stderr, code) = FsInteropToolbox.RunWsl(
          $"cd {w} && ewfacquire -u -q -t {set} -f {format} -c {compression} -S 1MiB -b 64 -C case -D test -E 1 -e examiner -N notes medium.raw " +
          $"&& ewfexport -u -q -f raw -t {set}-export {set}.E01 && cmp medium.raw {set}-export.raw");
        Assert.That(code, Is.Zero, $"{set}: {stdout}{stderr}");
        using var file = File.OpenRead(Path.Combine(dir, set + ".E01"));
        using var media = EwfMediaStream.Open(file);
        var bytes = new byte[media.Length];
        media.ReadExactly(bytes);
        Assert.That(Sha256(bytes), Is.EqualTo(Sha256(Medium())), set);
      }

      if (Environment.GetEnvironmentVariable("CWB_EWF_WRITE_VECTORS") == "1") {
        var vectors = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Ewf", "ReferenceVectors");
        Directory.CreateDirectory(vectors);
        foreach (var segment in Directory.GetFiles(dir, "encase?.E*")) {
          using var source = File.OpenRead(segment);
          using var target = File.Create(Path.Combine(vectors, Path.GetFileName(segment) + ".gz"));
          using var gzip = new GZipStream(target, CompressionLevel.SmallestSize);
          source.CopyTo(gzip);
        }
      }
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
