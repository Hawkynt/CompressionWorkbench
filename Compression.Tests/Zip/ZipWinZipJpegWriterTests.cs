using System.Reflection;
using Compression.Core.Entropy.WinZipJpeg;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>Writing ZIP method 96: our payloads must decode back to the JPEG, here and in unar.</summary>
[TestFixture]
public class ZipWinZipJpegWriterTests {
  private static byte[] Jpeg() {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZipWinZipJpegVectors.method96-project-authored.jpg")!;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] Zip(byte[] data, string name = "image.jpg") {
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true)) writer.AddEntry(name, data, ZipCompressionMethod.Jpeg);
    return ms.ToArray();
  }

  [Test, Category("RoundTrip")]
  public void Given_BaselineJpeg_When_Encoded_Then_DecoderRestoresItByteForByte() {
    var jpeg = Jpeg();
    Assert.That(WinZipJpegEncoder.TryEncode(jpeg, out var payload), Is.True);
    Assert.That(payload.Length, Is.LessThan(jpeg.Length));
    Assert.That(WinZipJpegDecoder.Decode(payload), Is.EqualTo(jpeg));
  }

  [Test, Category("RoundTrip")]
  public void Given_BaselineJpeg_When_ZippedWithMethod96_Then_ReaderExtractsIt() {
    var jpeg = Jpeg();
    using var reader = new ZipReader(new MemoryStream(Zip(jpeg)));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Jpeg));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(jpeg));
  }

  [Test, Category("Boundary")]
  public void Given_JpegWithTrailingBytes_When_Encoded_Then_TrailerSurvives() {
    byte[] jpeg = [.. Jpeg(), .. "trailer"u8];
    Assert.That(WinZipJpegEncoder.TryEncode(jpeg, out var payload), Is.True);
    Assert.That(WinZipJpegDecoder.Decode(payload), Is.EqualTo(jpeg));
  }

  [Test, Category("Exception")]
  public void Given_ProgressiveMarker_When_Encoded_Then_Refused() {
    var jpeg = Jpeg();
    var sof = jpeg.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xC0]);
    jpeg[sof + 1] = 0xC2;
    Assert.That(WinZipJpegEncoder.TryEncode(jpeg, out _), Is.False);
  }

  [Test, Category("Exception")]
  public void Given_NotAJpeg_When_ZippedWithMethod96_Then_DeflateIsUsedInstead() {
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("not a jpeg at all ", 50)));
    Assert.That(WinZipJpegEncoder.TryEncode(text, out _), Is.False);
    using var reader = new ZipReader(new MemoryStream(Zip(text, "a.txt")));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Deflate));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(text));
  }

  [Test, Category("Exception")]
  public void Given_ZeroPaddedScan_When_Encoded_Then_Refused() {
    // Clearing the bits before EOI makes the padding non-canonical: method 96 would rebuild one bits.
    var jpeg = Jpeg();
    var eoi = jpeg.AsSpan().LastIndexOf((ReadOnlySpan<byte>)[0xFF, 0xD9]);
    if ((jpeg[eoi - 1] & 1) == 0) Assert.Ignore("last scan byte ends in a data bit already");
    jpeg[eoi - 1] &= 0xFE;
    Assert.That(WinZipJpegEncoder.TryEncode(jpeg, out _), Is.False);
  }
}

/// <summary>
/// JPEGs cjpeg (libjpeg-turbo) makes in several shapes, zipped with method 96 by us, must come
/// out of unar byte for byte; progressive ones fall back to Deflate.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class ZipWinZipJpegWriterUnarTests {
  private static readonly (string Name, string Options, bool Method96)[] Cases = [
    ("q75-420", "-quality 75", true),
    ("q95-444", "-quality 95 -sample 1x1", true),
    ("q100", "-quality 100", true),
    ("optimized", "-quality 80 -optimize", true),
    ("restart", "-quality 85 -restart 1", true),
    ("restart-blocks", "-quality 85 -restart 7B", true),
    ("gray", "-quality 70 -grayscale", true),
    ("progressive", "-quality 80 -progressive", false),
  ];

  [Test]
  public void Given_CjpegImages_When_ZippedWithMethod96_Then_UnarExtractsThemByteForByte() {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL/Linux shell unavailable.");
    foreach (var tool in new[] { "unar", "cjpeg", "python3" })
      if (!FsInteropToolbox.WslHasTool(tool)) Assert.Ignore($"'{tool}' is required.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_wzjw_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var w = FsInteropToolbox.WinToWsl(dir);
      // A deterministic 401x297 colour image (odd sizes exercise MCU padding; large enough that
      // method 96 beats the original size, so the writer keeps it).
      var make = "python3 -c \"import math,sys;w,h=401,297;sys.stdout.buffer.write(b'P6 %d %d 255\\n'%(w,h)+bytes(int(127+120*math.sin((x*3+y*(c+1))*0.07+c)) for y in range(h) for x in range(w) for c in range(3)))\" > in.ppm";
      var jobs = string.Join(" && ", Cases.Select(c => $"cjpeg {c.Options} in.ppm > {c.Name}.jpg"));
      var r = FsInteropToolbox.RunWsl($"cd {w} && {make} && {jobs}");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdErr);

      using (var fs = File.Create(Path.Combine(dir, "ours.zip")))
      using (var writer = new ZipWriter(fs))
        foreach (var c in Cases) writer.AddEntry(c.Name + ".jpg", File.ReadAllBytes(Path.Combine(dir, c.Name + ".jpg")), ZipCompressionMethod.Jpeg);

      using (var reader = new ZipReader(File.OpenRead(Path.Combine(dir, "ours.zip"))))
        foreach (var c in Cases) {
          var entry = reader.Entries.Single(e => e.FileName == c.Name + ".jpg");
          Assert.That(entry.CompressionMethod == ZipCompressionMethod.Jpeg, Is.EqualTo(c.Method96), c.Name);
        }

      r = FsInteropToolbox.RunWsl($"cd {w} && unar -q -f -o out ours.zip");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
      foreach (var c in Cases)
        Assert.That(File.ReadAllBytes(Directory.GetFiles(Path.Combine(dir, "out"), c.Name + ".jpg", SearchOption.AllDirectories).Single()), Is.EqualTo(File.ReadAllBytes(Path.Combine(dir, c.Name + ".jpg"))), c.Name);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
