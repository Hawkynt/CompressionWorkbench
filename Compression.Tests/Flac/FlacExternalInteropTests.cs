#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Diagnostics;
using FileFormat.Flac;

namespace Compression.Tests.Flac;

/// <summary>
/// Verifies our FLAC codec interoperates with the reference <c>flac</c> CLI
/// (libFLAC) and <c>ffmpeg</c>. Gated by <see cref="Assert.Ignore"/> when the
/// external tools aren't on PATH so CI without them stays green.
///
/// Covers TODO.md Section 6a: our FLAC codec was implemented from scratch but
/// never validated against any external decoder.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class FlacExternalInteropTests {

  // 2 seconds @ 44.1 kHz, stereo 16-bit. ~352 kB raw — kept small enough to
  // run quickly under the suite's per-test budget.
  private const int SampleRate = 44100;
  private const int Channels = 2;
  private const int BitsPerSample = 16;
  private const int DurationSeconds = 2;
  private const int TotalSamples = SampleRate * DurationSeconds;

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_flac_interop_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Test data ──────────────────────────────────────────────────────

  /// <summary>
  /// Deterministic 2s stereo 16-bit PCM @ 44.1 kHz: left = 440 Hz sine,
  /// right = 880 Hz sine, both at ~30000 amplitude. Returns raw interleaved
  /// little-endian PCM bytes (no WAV header).
  /// </summary>
  private static byte[] GeneratePcm() {
    var pcm = new byte[TotalSamples * Channels * (BitsPerSample / 8)];
    for (var i = 0; i < TotalSamples; i++) {
      var t = i / (double)SampleRate;
      var left = (short)(Math.Sin(2.0 * Math.PI * 440.0 * t) * 30000);
      var right = (short)(Math.Sin(2.0 * Math.PI * 880.0 * t) * 30000);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 0), left);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), right);
    }
    return pcm;
  }

  /// <summary>
  /// Wraps raw PCM in a canonical RIFF/WAV container so the reference
  /// <c>flac</c> encoder can read it.
  /// </summary>
  private static byte[] WrapAsWav(byte[] pcm) {
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    var byteRate = SampleRate * Channels * BitsPerSample / 8;
    var blockAlign = (short)(Channels * BitsPerSample / 8);
    bw.Write("RIFF"u8);
    bw.Write(36 + pcm.Length);
    bw.Write("WAVE"u8);
    bw.Write("fmt "u8);
    bw.Write(16);                            // PCM fmt chunk size
    bw.Write((short)1);                      // PCM format
    bw.Write((short)Channels);
    bw.Write(SampleRate);
    bw.Write(byteRate);
    bw.Write(blockAlign);
    bw.Write((short)BitsPerSample);
    bw.Write("data"u8);
    bw.Write(pcm.Length);
    bw.Write(pcm);
    return ms.ToArray();
  }

  /// <summary>
  /// Strips the RIFF header from a canonical PCM WAV file produced by the
  /// reference decoder and returns just the sample bytes. We tolerate
  /// non-canonical chunk order (e.g. extra LIST/INFO chunks the encoder might
  /// have stuffed back) by walking sub-chunks until we find <c>data</c>.
  /// </summary>
  private static byte[] ExtractWavPcm(byte[] wav) {
    Assert.That(wav.Length, Is.GreaterThan(44), "WAV file too short");
    Assert.That(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8), Is.True, "Missing RIFF magic");
    Assert.That(wav.AsSpan(8, 4).SequenceEqual("WAVE"u8), Is.True, "Missing WAVE magic");
    var pos = 12;
    while (pos + 8 <= wav.Length) {
      var chunkId = wav.AsSpan(pos, 4);
      var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 4, 4));
      pos += 8;
      if (chunkId.SequenceEqual("data"u8))
        return wav.AsSpan(pos, chunkSize).ToArray();
      pos += chunkSize;
      if ((chunkSize & 1) == 1) pos++; // RIFF pad byte
    }
    Assert.Fail("No data chunk found in WAV");
    return null!;
  }

  // ── Scenarios ──────────────────────────────────────────────────────

  /// <summary>
  /// Given: deterministic stereo PCM encoded by us.
  /// When:  reference <c>flac -d</c> decodes our .flac.
  /// Then:  PCM payload from the reference decoder matches the original.
  /// </summary>
  [Test]
  public void OurEncode_ReferenceDecode() {
    var pcm = GeneratePcm();
    var flacPath = Path.Combine(this._tmpDir, "ours.flac");
    using (var input = new MemoryStream(pcm))
    using (var output = File.Create(flacPath)) {
      FlacWriter.Compress(input, output);
    }
    var outWavPath = Path.Combine(this._tmpDir, "ref_decoded.wav");
    RunTool("flac", $"-d --totally-silent -f --output-name=\"{outWavPath}\" \"{flacPath}\"");
    Assert.That(File.Exists(outWavPath), Is.True, "Reference decoder produced no output");
    var decodedPcm = ExtractWavPcm(File.ReadAllBytes(outWavPath));
    Assert.That(decodedPcm, Is.EqualTo(pcm), "Reference-decoded PCM differs from original");
  }

  /// <summary>
  /// Given: deterministic stereo PCM wrapped as a WAV and encoded by the
  ///        reference <c>flac -8</c>.
  /// When:  our reader decodes that .flac.
  /// Then:  decoded PCM matches the original.
  /// </summary>
  [Test]
  public void ReferenceEncode_OurDecode() {
    var pcm = GeneratePcm();
    var wavPath = Path.Combine(this._tmpDir, "src.wav");
    File.WriteAllBytes(wavPath, WrapAsWav(pcm));
    var flacPath = Path.Combine(this._tmpDir, "ref.flac");
    RunTool("flac", $"-8 --totally-silent -f --output-name=\"{flacPath}\" \"{wavPath}\"");
    Assert.That(File.Exists(flacPath), Is.True, "Reference encoder produced no output");

    using var input = File.OpenRead(flacPath);
    using var output = new MemoryStream();
    FlacReader.Decompress(input, output);
    Assert.That(output.ToArray(), Is.EqualTo(pcm), "Our decoder differs from original PCM");
  }

  /// <summary>
  /// Given: deterministic stereo PCM encoded by us.
  /// When:  <c>ffmpeg</c> decodes our .flac to raw signed 16-bit LE PCM.
  /// Then:  ffmpeg-decoded PCM matches the original.
  /// </summary>
  [Test]
  public void OurEncode_FfmpegDecode() {
    var pcm = GeneratePcm();
    var flacPath = Path.Combine(this._tmpDir, "ours.flac");
    using (var input = new MemoryStream(pcm))
    using (var output = File.Create(flacPath)) {
      FlacWriter.Compress(input, output);
    }
    var rawPath = Path.Combine(this._tmpDir, "ffmpeg_out.pcm");
    RunTool("ffmpeg", $"-hide_banner -loglevel error -y -i \"{flacPath}\" -f s16le -acodec pcm_s16le \"{rawPath}\"");
    Assert.That(File.Exists(rawPath), Is.True, "ffmpeg produced no output");
    var decodedPcm = File.ReadAllBytes(rawPath);
    Assert.That(decodedPcm, Is.EqualTo(pcm), "ffmpeg-decoded PCM differs from original");
  }

  /// <summary>
  /// Given: the same PCM encoded by us and by <c>flac -8</c>.
  /// Then:  our output is at most 50% larger than the reference. We don't
  ///        need to beat libFLAC, just stay in the same order of magnitude.
  /// </summary>
  [Test]
  public void CompressionRatio_RoughlyComparable() {
    var pcm = GeneratePcm();

    // Our encoder.
    var oursPath = Path.Combine(this._tmpDir, "ours.flac");
    using (var input = new MemoryStream(pcm))
    using (var output = File.Create(oursPath)) {
      FlacWriter.Compress(input, output);
    }
    var ourSize = new FileInfo(oursPath).Length;

    // Reference encoder.
    var wavPath = Path.Combine(this._tmpDir, "src.wav");
    File.WriteAllBytes(wavPath, WrapAsWav(pcm));
    var refPath = Path.Combine(this._tmpDir, "ref.flac");
    RunTool("flac", $"-8 --totally-silent -f --output-name=\"{refPath}\" \"{wavPath}\"");
    var refSize = new FileInfo(refPath).Length;

    TestContext.Out.WriteLine($"PCM input:  {pcm.Length,8} bytes");
    TestContext.Out.WriteLine($"Ours:       {ourSize,8} bytes  ({100.0 * ourSize / pcm.Length:F1}%)");
    TestContext.Out.WriteLine($"flac -8:    {refSize,8} bytes  ({100.0 * refSize / pcm.Length:F1}%)");
    TestContext.Out.WriteLine($"Overhead:   {100.0 * (ourSize - refSize) / refSize:+0.0;-0.0;0.0}% vs reference");

    Assert.That(ourSize, Is.LessThanOrEqualTo(refSize * 3 / 2),
      $"Our FLAC is {ourSize} bytes vs reference {refSize} bytes — more than 50% overhead");
  }

  /// <summary>
  /// Given: our FLAC output.
  /// When:  <c>flac --test</c> validates the bitstream + MD5 chunk.
  /// Then:  reference exits 0, meaning our STREAMINFO MD5 matches the
  ///        decoded PCM (the canonical FLAC integrity test).
  /// </summary>
  [Test]
  public void Md5_StoredAndVerifiableByReference() {
    var pcm = GeneratePcm();
    var flacPath = Path.Combine(this._tmpDir, "ours.flac");
    using (var input = new MemoryStream(pcm))
    using (var output = File.Create(flacPath)) {
      FlacWriter.Compress(input, output);
    }
    // `flac --test` returns 0 iff the bitstream parses cleanly AND the MD5
    // signature in STREAMINFO matches the MD5 of the decoded PCM.
    RunTool("flac", $"--test --totally-silent \"{flacPath}\"");
  }

  // ── Process plumbing ──────────────────────────────────────────────

  /// <summary>
  /// Runs <paramref name="tool"/> from PATH. Ignores the test if the tool is
  /// not installed; fails the test if the tool returns non-zero.
  /// </summary>
  private static (string StdOut, string StdErr, int ExitCode) RunTool(string tool, string args) {
    if (!IsToolOnPath(tool))
      Assert.Ignore($"Tool not found on PATH: {tool}");

    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit(60_000);
    if (proc.ExitCode != 0)
      Assert.Fail($"{tool} {args} exited with code {proc.ExitCode}.\nstdout: {stdout}\nstderr: {stderr}");
    return (stdout, stderr, proc.ExitCode);
  }

  private static bool IsToolOnPath(string tool) {
    var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
    var pathExt = OperatingSystem.IsWindows()
      ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD").Split(';')
      : [""];
    foreach (var dir in pathEnv.Split(Path.PathSeparator)) {
      if (string.IsNullOrWhiteSpace(dir)) continue;
      foreach (var ext in pathExt) {
        try {
          var candidate = Path.Combine(dir, tool + ext);
          if (File.Exists(candidate)) return true;
        } catch {
          // dir contains invalid path chars — skip.
        }
      }
    }
    return false;
  }
}
