using System.Diagnostics;
using System.Text;
using FileFormat.Btoa;

namespace Compression.Tests.Btoa;

/// <summary>
/// btoa 4.0. The committed vectors are btoa 4.0's own output (see Btoa/Vectors/README.md):
/// our encoder must write the same bytes and our decoder must restore the inputs. The
/// live test runs the real btoa/atob both ways when they are built.
/// </summary>
[TestFixture]
public class BtoaTests {
  private static readonly string[] Vectors = ["empty", "one", "hello", "zeros312", "ff64", "bytes1024", "random5003"];

  private static byte[] Load(string name) {
    using var s = typeof(BtoaTests).Assembly.GetManifestResourceStream("BtoaVectors." + name)
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] Encode(byte[] data) {
    using var ms = new MemoryStream();
    BtoaCodec.Encode(data, ms);
    return ms.ToArray();
  }

  // ---- the real program's output ----------------------------------------------------

  [Test, Category("HappyPath")]
  public void Encode_GivenTheVectorInput_ThenTheBytesEqualBtoa40([ValueSource(nameof(Vectors))] string name) =>
    Assert.That(Encode(Load(name + ".bin")), Is.EqualTo(Load(name + ".btoa")).AsCollection);

  [Test, Category("HappyPath")]
  public void Decode_GivenBtoa40Output_ThenTheInputComesBack([ValueSource(nameof(Vectors))] string name) =>
    Assert.That(BtoaCodec.Decode(Load(name + ".btoa")), Is.EqualTo(Load(name + ".bin")).AsCollection);

  [Test, Category("HappyPath")]
  public void Descriptor_GivenBtoa40Output_WhenDecompressed_ThenTheInputComesBack() {
    var descriptor = new BtoaFormatDescriptor();
    using var input = new MemoryStream(Load("random5003.btoa"));
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    Assert.That(output.ToArray(), Is.EqualTo(Load("random5003.bin")).AsCollection);
  }

  // ---- round trip, boundaries ---------------------------------------------------------

  [Test, Category("RoundTrip")]
  public void RoundTrip_GivenEveryLengthUpToTwoLines_ThenTheDataComesBack() {
    var rng = new Random(7);
    for (var length = 0; length <= 130; ++length) {
      var data = new byte[length];
      rng.NextBytes(data);
      if (length % 3 == 0)
        data.AsSpan(0, length / 2).Clear(); // zero words, so 'z' groups mix with digits
      Assert.That(BtoaCodec.Decode(Encode(data)), Is.EqualTo(data).AsCollection, $"length {length}");
    }
  }

  [Test, Category("Boundary")]
  public void Encode_GivenExactly78Characters_ThenABlankLineSeparatesTheTrailer() {
    var text = Encoding.ASCII.GetString(Encode(new byte[312]));
    Assert.That(text, Is.EqualTo("xbtoa Begin\n" + new string('z', 78) + "\n\nxbtoa End N 312 138 E 0 S 138 R 0\n"));
  }

  [Test, Category("Boundary")]
  public void Decode_GivenCrLfLineEnds_ThenTheyAreIgnored() {
    var crlf = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Load("bytes1024.btoa")).Replace("\n", "\r\n"));
    Assert.That(BtoaCodec.Decode(crlf), Is.EqualTo(Load("bytes1024.bin")).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Decode_GivenTextBeforeTheBeginLine_ThenItIsSkipped() {
    var mail = Encoding.ASCII.GetBytes("From: someone\nSubject: data\n\n").Concat(Load("hello.btoa")).ToArray();
    Assert.That(BtoaCodec.Decode(mail), Is.EqualTo(Load("hello.bin")).AsCollection);
  }

  // ---- refusal ----------------------------------------------------------------------------

  private static byte[] Mutate(string name, Func<string, string> change) =>
    Encoding.ASCII.GetBytes(change(Encoding.ASCII.GetString(Load(name))));

  [Test, Category("Exceptional")]
  public void Decode_GivenAChangedDigit_ThenTheChecksumRefusesIt() {
    // '8' -> '9' keeps the group valid but changes the data.
    var bad = Mutate("hello.btoa", t => "xbtoa Begin\n9" + t["xbtoa Begin\n8".Length..]);
    Assert.That(() => BtoaCodec.Decode(bad), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exceptional")]
  public void Decode_GivenAWrongByteCount_ThenItIsRefused() {
    var bad = Mutate("hello.btoa", t => t.Replace("N 13 d", "N 12 c"));
    Assert.That(() => BtoaCodec.Decode(bad), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exceptional")]
  public void Decode_GivenDisagreeingCounts_ThenItIsRefused() {
    var bad = Mutate("hello.btoa", t => t.Replace("N 13 d", "N 13 e"));
    Assert.That(() => BtoaCodec.Decode(bad), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exceptional")]
  public void Decode_GivenNoBeginLine_ThenItIsRefused() =>
    Assert.That(() => BtoaCodec.Decode("87cUR\nxbtoa End N 0 0 E 0 S 0 R 0\n"u8.ToArray()), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exceptional")]
  public void Decode_GivenNoTrailer_ThenItIsRefused() =>
    Assert.That(() => BtoaCodec.Decode("xbtoa Begin\n87cUR\n"u8.ToArray()), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exceptional")]
  public void Decode_GivenACharacterOutsideTheAlphabet_ThenItIsRefused() =>
    Assert.That(() => BtoaCodec.Decode("xbtoa Begin\n87c~R\nxbtoa End N 4 4 E 0 S 4 R 0\n"u8.ToArray()), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exceptional")]
  public void Decode_GivenZInsideAGroup_ThenItIsRefused() =>
    Assert.That(() => BtoaCodec.Decode("xbtoa Begin\n87zUR\nxbtoa End N 4 4 E 0 S 4 R 0\n"u8.ToArray()), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exceptional")]
  public void Decode_GivenAGroupAbove32Bits_ThenItIsRefused() =>
    Assert.That(() => BtoaCodec.Decode("xbtoa Begin\nuuuuu\nxbtoa End N 4 4 E 0 S 4 R 0\n"u8.ToArray()), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exceptional")]
  public void Decode_GivenATruncatedGroup_ThenItIsRefused() =>
    Assert.That(() => BtoaCodec.Decode("xbtoa Begin\n87cU\nxbtoa End N 3 3 E 0 S 3 R 0\n"u8.ToArray()), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exceptional")]
  public void Decode_GivenAMalformedTrailer_ThenItIsRefused() {
    var bad = Mutate("hello.btoa", t => t.Replace(" R ", " Q "));
    Assert.That(() => BtoaCodec.Decode(bad), Throws.InstanceOf<InvalidDataException>());
  }

  // ---- live oracle ------------------------------------------------------------------------

  /// <summary>
  /// The real btoa/atob 4.0 (built as described in Btoa/Vectors/README.md) both ways: atob
  /// restores what we write, and we restore what btoa writes. <c>CWB_BTOA_DIR</c> names the
  /// Linux directory holding the two binaries (reached through WSL on Windows).
  /// </summary>
  [Test, Category("ExternalInterop")]
  public void RealBtoa_GivenRandomData_ThenBothDirectionsAgree() {
    var dir = Environment.GetEnvironmentVariable("CWB_BTOA_DIR");
    if (string.IsNullOrWhiteSpace(dir))
      Assert.Ignore("Set CWB_BTOA_DIR to the Linux directory holding btoa and atob 4.0.");

    var work = Path.Combine(Path.GetTempPath(), "cwb_btoa_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var rng = new Random(42);
      foreach (var length in new[] { 0, 1, 77, 312, 4099, 65537 }) {
        var data = new byte[length];
        rng.NextBytes(data);
        File.WriteAllBytes(Path.Combine(work, "in.bin"), data);
        File.WriteAllBytes(Path.Combine(work, "ours.btoa"), Encode(data));

        var w = Linux(work);
        var (_, err, code) = Shell($"cd '{w}' && '{dir}/atob' < ours.btoa > theirs.bin && '{dir}/btoa' < in.bin > theirs.btoa");
        Assert.That(code, Is.EqualTo(0), err);
        Assert.Multiple(() => {
          Assert.That(File.ReadAllBytes(Path.Combine(work, "theirs.bin")), Is.EqualTo(data).AsCollection, $"atob on ours, {length} bytes");
          Assert.That(BtoaCodec.Decode(File.ReadAllBytes(Path.Combine(work, "theirs.btoa"))), Is.EqualTo(data).AsCollection, $"ours on btoa, {length} bytes");
          Assert.That(File.ReadAllBytes(Path.Combine(work, "theirs.btoa")), Is.EqualTo(Encode(data)).AsCollection, $"same text, {length} bytes");
        });
      }
    } finally {
      try { Directory.Delete(work, true); } catch (IOException) { }
    }
  }

  private static string Linux(string path) => OperatingSystem.IsWindows() ? FsInteropToolbox.WinToWsl(path) : path;

  private static (string Out, string Err, int Code) Shell(string command) {
    if (OperatingSystem.IsWindows())
      return FsInteropToolbox.RunWsl(command);
    var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add(command);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (stdout.Result, stderr.Result, p.ExitCode);
  }
}
