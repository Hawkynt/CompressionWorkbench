using System.Buffers.Binary;
using System.Diagnostics;
using FileFormat.Lzma;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Lzma;

/// <summary>
/// The <c>.lzma</c> (LZMA alone) container against liblzma and 7-Zip. We used to write the
/// uncompressed size in the header and an end marker after the data. 7-Zip accepts that, but
/// liblzma rejects it as corrupt. The LZMA SDK's encoder writes either a known size or an end
/// marker, never both, and so do we now.
/// </summary>
[TestFixture]
public sealed class LzmaAloneInteropTests {

  private static byte[] Compress(byte[] data) {
    using var output = new MemoryStream();
    LzmaStream.Compress(new MemoryStream(data), output);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] stream) {
    using var output = new MemoryStream();
    LzmaStream.Decompress(new MemoryStream(stream), output);
    return output.ToArray();
  }

  private static byte[] Text(int length) {
    var words = "the quick brown fox jumps over the lazy dog while the range coder narrows its interval "u8.ToArray();
    var random = new Random(length);
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = random.Next(9) == 0 ? (byte)random.Next(256) : words[(i * 3 + i / 11) % words.Length];
    return data;
  }

  [Test, Category("HappyPath")]
  public void GivenTheStreamLiblzmaWrites_WhenDecoded_ThenItIsTheInput() {
    // lzma.compress(b"abracadabra abracadabra", format=lzma.FORMAT_ALONE): unknown size, end marker.
    var stream = Convert.FromHexString("5d00008000ffffffffffffffff0030988aaa9a59f511d83eb115880b8fffffea430000");
    Assert.That(Decompress(stream), Is.EqualTo("abracadabra abracadabra"u8.ToArray()));
  }

  [TestCase(1, TestName = "GivenOneByte_WhenCompressed_ThenTheHeaderStatesItsSize")]
  [TestCase(50_000, TestName = "GivenText_WhenCompressed_ThenTheHeaderStatesItsSize")]
  [Category("HappyPath")]
  public void TheHeaderStatesTheSize(int length) {
    var data = Text(length);
    var stream = Compress(data);
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadInt64LittleEndian(stream.AsSpan(5)), Is.EqualTo(length));
      Assert.That(Decompress(stream), Is.EqualTo(data));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenOurStream_WhenItsSizeIsErased_ThenNoEndMarkerFollowsTheData() {
    var data = Text(20_000);
    var stream = Compress(data);
    BinaryPrimitives.WriteInt64LittleEndian(stream.AsSpan(5), -1);
    // With the size gone, a decoder has to find an end marker; ours must not be there.
    Assert.That(() => Decompress(stream), Throws.InstanceOf<Exception>());
  }

  // ── Third-party readers: liblzma (CPython's lzma module) and 7-Zip ──────

  private static readonly Lazy<string?> Python = new(() => {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        var start = new ProcessStartInfo(candidate) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import lzma");
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on PATH
      }
    }
    return null;
  });

  [TestCase(0, TestName = "GivenNoBytes_WhenOurLzmaIsDecodedByLiblzma_ThenItIsEmpty")]
  [TestCase(1, TestName = "GivenOneByte_WhenOurLzmaIsDecodedByLiblzma_ThenItIsTheInput")]
  [TestCase(200_000, TestName = "GivenText_WhenOurLzmaIsDecodedByLiblzma_ThenItIsTheInput")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void LiblzmaReadsOurLzma(int length) {
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with the lzma module on the PATH.");
    var data = Text(length);
    var input = Path.GetTempFileName();
    var output = Path.GetTempFileName();
    try {
      File.WriteAllBytes(input, Compress(data));
      var start = new ProcessStartInfo(python!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
      start.ArgumentList.Add("-c");
      start.ArgumentList.Add("import sys, lzma; open(sys.argv[2], 'wb').write(lzma.decompress(open(sys.argv[1], 'rb').read(), format=lzma.FORMAT_ALONE))");
      start.ArgumentList.Add(input);
      start.ArgumentList.Add(output);
      using var process = Process.Start(start)!;
      var stderr = process.StandardError.ReadToEndAsync();
      process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      Assert.That(process.ExitCode, Is.Zero, stderr.Result);
      Assert.That(File.ReadAllBytes(output), Is.EqualTo(data));
    } finally {
      File.Delete(input);
      File.Delete(output);
    }
  }

  [Test, Category("ArchiveExternal"), Category("EndToEnd")]
  public void GivenText_WhenOurLzmaIsExtractedBySevenZip_ThenItIsTheInput() {
    if (!FsInteropToolbox.SevenZipAvailable) Assert.Ignore("7-Zip not available.");
    var data = Text(120_000);
    var work = Path.Combine(Path.GetTempPath(), $"cwb_lzma_{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try {
      var path = Path.Combine(work, "data.lzma");
      File.WriteAllBytes(path, Compress(data));
      var (stdout, stderr, exit) = FsInteropToolbox.Run7z($"x -y -o\"{Path.Combine(work, "out")}\" \"{path}\"");
      Assert.That(exit, Is.Zero, stdout + stderr);
      Assert.That(File.ReadAllBytes(Path.Combine(work, "out", "data")), Is.EqualTo(data));
    } finally {
      Directory.Delete(work, true);
    }
  }
}
