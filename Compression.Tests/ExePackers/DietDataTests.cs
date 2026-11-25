using System.Diagnostics;
using System.Text;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

/// <summary>
/// DIET data files. The committed vectors are DIET 1.45f's own output (see
/// ExePackers/DietDataVectors/README.md); the live test hands our files to DIET 1.45f's
/// <c>DIET -R</c> and DIET's to us, in DOSBox-X run headless inside WSL.
/// </summary>
[TestFixture]
public class DietDataTests {
  private static readonly string[] Vectors = ["T16K", "ODD", "Z50K", "MIX20K"];

  private static byte[] Load(string name) {
    using var s = typeof(DietDataTests).Assembly.GetManifestResourceStream("DietDataVectors." + name)
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] Text(int length, int seed) {
    var words = new[] { "alpha ", "beta ", "gamma ", "compress ", "data ", "DIET ", "\r\n", "the ", "of " };
    var rng = new Random(seed);
    var sb = new StringBuilder();
    while (sb.Length < length)
      sb.Append(words[rng.Next(words.Length)]);
    return Encoding.ASCII.GetBytes(sb.ToString(0, length));
  }

  // ---- DIET's own files -------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Decode_GivenDiet145fOutput_ThenTheOriginalComesBack([ValueSource(nameof(Vectors))] string name) =>
    Assert.That(DietDataCodec.Decode(Load(name + ".diet")), Is.EqualTo(Load(name + ".bin")).AsCollection);

  [Test, Category("HappyPath")]
  public void Identify_GivenDiet145fOutput_ThenTheLayoutIs144To145() =>
    Assert.That(DietDataCodec.Identify(Load("T16K.diet")), Is.EqualTo("1.44-1.45"));

  [Test, Category("HappyPath")]
  public void Descriptor_GivenDiet145fOutput_WhenDecompressed_ThenTheOriginalComesBack() {
    using var input = new MemoryStream(Load("MIX20K.diet"));
    using var output = new MemoryStream();
    new DietDataFormatDescriptor().Decompress(input, output);
    Assert.That(output.ToArray(), Is.EqualTo(Load("MIX20K.bin")).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void Decode_GivenThe102Layout_ThenTheSameStreamDecodes() {
    // The 1.02-1.20 layout is the 1.44 header without its leading B4 4C CD 21.
    var file = Load("T16K.diet");
    var older = file.AsSpan(4).ToArray();
    Assert.Multiple(() => {
      Assert.That(DietDataCodec.Identify(older), Is.EqualTo("1.02-1.20"));
      Assert.That(DietDataCodec.Decode(older), Is.EqualTo(Load("T16K.bin")).AsCollection);
    });
  }

  // ---- our writer -------------------------------------------------------------------

  [Test, Category("RoundTrip")]
  public void RoundTrip_GivenVariedInputs_ThenTheDataComesBack() {
    var rng = new Random(3);
    List<byte[]> inputs = [
      [], [0x41], [0, 0], Text(3, 1), Text(1000, 2), Text(70000, 3), new byte[50000], Load("MIX20K.bin"),
    ];
    var random = new byte[9000];
    rng.NextBytes(random);
    inputs.Add(random);
    var runs = new byte[20000];
    for (var i = 0; i < runs.Length; ++i)
      runs[i] = (byte)(i / 300 % 7);
    inputs.Add(runs);

    foreach (var input in inputs)
      Assert.That(DietDataCodec.Decode(DietDataCodec.Encode(input)), Is.EqualTo(input).AsCollection, $"{input.Length} bytes");
  }

  [Test, Category("HappyPath")]
  public void Encode_GivenText_ThenItCompressesAboutAsWellAsDiet() {
    var original = Load("T16K.bin");
    var ours = DietDataCodec.Encode(original);
    Assert.That(ours.Length, Is.LessThan(Load("T16K.diet").Length * 1.25));
  }

  [Test, Category("Boundary")]
  public void Encode_GivenLongRunsAndFarMatches_ThenEveryLengthAndDistanceCodeRoundTrips() {
    // A block repeated at distances from 2 to 8191 with lengths 2 to 272 exercises every code.
    var data = new List<byte>();
    var rng = new Random(11);
    var block = new byte[300];
    rng.NextBytes(block);
    data.AddRange(block);
    foreach (var gap in new[] { 1, 200, 500, 1000, 2000, 4000, 7800 }) {
      var filler = new byte[gap];
      rng.NextBytes(filler);
      data.AddRange(filler);
      data.AddRange(block.AsSpan(0, Math.Min(272, block.Length)).ToArray());
    }
    var input = data.ToArray();
    Assert.That(DietDataCodec.Decode(DietDataCodec.Encode(input)), Is.EqualTo(input).AsCollection);
  }

  [Test, Category("Boundary")]
  public void Encode_GivenTheLargestOriginal_ThenTheHeaderCarriesAll22Bits() {
    var input = new byte[DietDataCodec.MaximumOriginalLength];
    var encoded = DietDataCodec.Encode(input);
    Assert.That(DietDataCodec.Decode(encoded).Length, Is.EqualTo(input.Length));
  }

  [Test, Category("Exceptional")]
  public void Encode_GivenMoreThan22BitsOfData_ThenItIsRefused() =>
    Assert.That(() => DietDataCodec.Encode(new byte[DietDataCodec.MaximumOriginalLength + 1]), Throws.ArgumentException);

  // ---- refusal ----------------------------------------------------------------------

  [Test, Category("Exceptional")]
  public void Decode_GivenACorruptedCompressedByte_ThenTheCrcRefusesIt() {
    var file = Load("T16K.diet");
    file[100] ^= 0x10;
    Assert.That(() => DietDataCodec.Decode(file), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exceptional")]
  public void Decode_GivenAWrongOriginalLength_ThenItIsRefused() {
    var file = Load("T16K.diet");
    file[15] ^= 0x01; // low byte of the original length
    Assert.That(() => DietDataCodec.Decode(file), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exceptional")]
  public void Decode_GivenATruncatedFile_ThenItIsRefused() =>
    Assert.That(() => DietDataCodec.Decode(Load("T16K.diet").AsSpan(0, 400)), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exceptional")]
  public void Decode_GivenTheMultiBlockFlag_ThenItIsRefused() {
    var file = Load("T16K.diet");
    file[9] |= 0x80;
    Assert.That(() => DietDataCodec.Decode(file), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exceptional")]
  public void Decode_GivenNoDietHeader_ThenItIsRefused() =>
    Assert.That(() => DietDataCodec.Decode("plain text, not DIET"u8), Throws.InstanceOf<InvalidDataException>());

  // ---- live oracle: DIET 1.45f in DOSBox-X under WSL, no window ------------------------

  /// <summary>
  /// Writes our files, has DIET 1.45f restore them with <c>DIET -R</c>, has it compress the
  /// originals with <c>DIET -B</c>, and checks both directions. Needs <c>CWB_TOOL_DIET</c>
  /// (DIET.EXE 1.45f) and WSL with dosbox-x; DOSBox-X runs with SDL's dummy video driver.
  /// </summary>
  [Test, Category("ExternalInterop")]
  public void RealDiet_GivenOurFilesAndItsFiles_ThenBothDirectionsRestoreTheOriginals() {
    var diet = Environment.GetEnvironmentVariable("CWB_TOOL_DIET");
    if (string.IsNullOrWhiteSpace(diet) || !File.Exists(diet))
      Assert.Ignore("Set CWB_TOOL_DIET to DIET.EXE 1.45f.");
    if (!OperatingSystem.IsWindows() ? !ToolOnPath("dosbox-x") : !FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("dosbox-x"))
      Assert.Ignore("dosbox-x is not available (in WSL on Windows).");

    var inputs = new Dictionary<string, byte[]> {
      ["TEXT1.TXT"] = Text(16000, 21),
      ["TEXT2.TXT"] = Text(70000, 22),
      ["TEXT3.TXT"] = Text(300000, 23),
      ["ZERO.BIN"] = new byte[40000],
      ["MIX.BIN"] = Load("MIX20K.bin"),
      ["SHORT.TXT"] = Text(200, 24),
    };

    var drive = Path.Combine(Path.GetTempPath(), "cwb_dietdata_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(drive);
    try {
      File.Copy(diet!, Path.Combine(drive, "DIET.EXE"));
      var lines = new List<string> { "@echo off" };
      var n = 0;
      foreach (var (name, data) in inputs) {
        ++n;
        File.WriteAllBytes(Path.Combine(drive, $"O{n}.DAT"), DietDataCodec.Encode(data));
        File.WriteAllBytes(Path.Combine(drive, $"P{n}.DAT"), data);
        lines.Add($"DIET -R O{n}.DAT >> LOG.TXT");
        lines.Add($"DIET -B P{n}.DAT >> LOG.TXT");
      }
      File.WriteAllText(Path.Combine(drive, "GO.BAT"), string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);

      var dir = OperatingSystem.IsWindows() ? FsInteropToolbox.WinToWsl(drive) : drive;
      var command = "unset WAYLAND_DISPLAY; export SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy; " +
        $"timeout 600 dosbox-x -silent -nolog -fastlaunch -defaultconf -set 'cpu cycles=max' -c 'mount c \"{dir}\"' -c c: -c 'call GO.BAT' -c exit";
      var (_, err, code) = Shell(command);
      Assert.That(code, Is.EqualTo(0), err);

      n = 0;
      Assert.Multiple(() => {
        foreach (var (name, data) in inputs) {
          ++n;
          Assert.That(File.ReadAllBytes(Path.Combine(drive, $"O{n}.DAT")), Is.EqualTo(data).AsCollection, $"DIET -R on ours: {name}");
          var theirs = File.ReadAllBytes(Path.Combine(drive, $"P{n}.DAT"));
          if (DietDataCodec.Identify(theirs) is not null)
            Assert.That(DietDataCodec.Decode(theirs), Is.EqualTo(data).AsCollection, $"ours on DIET's: {name}");
          else
            Assert.That(theirs, Is.EqualTo(data).AsCollection, $"DIET left {name} uncompressed");
        }
      });
    } finally {
      try { Directory.Delete(drive, true); } catch (IOException) { }
    }
  }

  private static bool ToolOnPath(string tool) => Shell($"command -v {tool}").Code == 0;

  private static (string Out, string Err, int Code) Shell(string command) {
    if (OperatingSystem.IsWindows())
      return FsInteropToolbox.RunWsl(command, TimeSpan.FromMinutes(11)) is var (o, e, c, _) ? (o, e, c) : default;
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
