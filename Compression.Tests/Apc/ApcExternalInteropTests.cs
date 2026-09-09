#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Diagnostics;
using Compression.Registry;
using FileFormat.Apc;

namespace Compression.Tests.Apc;

[TestFixture]
[Category("ExternalInterop")]
[Category("AudioExternalInterop")]
public sealed class ApcExternalInteropTests {

  private const int SampleRate = 22050;
  private const int Frames = 512;
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_apc_interop_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  [TestCase(1)]
  [TestCase(2)]
  public void OurApcWriter_IsDecodedByFfmpegWithExpectedLength(int channels) {
    RequireFfmpeg();
    var pcm = GeneratePcm(channels);
    var descriptor = new ApcFormatDescriptor();
    using var encoded = new MemoryStream();
    descriptor.EncodePcm(encoded, pcm, "ima-adpcm-apc", new FormatCreateOptions());

    var inputPath = Path.Combine(this._tmpDir, $"ours-{channels}ch.apc");
    var outputPath = Path.Combine(this._tmpDir, $"decoded-{channels}ch.pcm");
    File.WriteAllBytes(inputPath, encoded.ToArray());

    var result = Run("ffmpeg", "-hide_banner", "-v", "error", "-y", "-i", inputPath,
      "-f", "s16le", "-acodec", "pcm_s16le", outputPath);

    Assert.Multiple(() => {
      Assert.That(result.ExitCode, Is.Zero, result.StdErr);
      Assert.That(File.Exists(outputPath), Is.True, result.StdErr);
      if (File.Exists(outputPath))
        Assert.That(new FileInfo(outputPath).Length, Is.EqualTo((long)Frames * channels * 2));
    });
  }

  private static AudioPcmBuffer GeneratePcm(int channels) {
    var data = new byte[Frames * channels * 2];
    for (var frame = 0; frame < Frames; ++frame) {
      var t = frame / (double)SampleRate;
      for (var channel = 0; channel < channels; ++channel) {
        var frequency = channel == 0 ? 440.0 : 880.0;
        var sample = (short)(Math.Sin(2 * Math.PI * frequency * t) * 28000);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan((frame * channels + channel) * 2), sample);
      }
    }
    return new AudioPcmBuffer(new AudioPcmFormat(SampleRate, channels, 16), data);
  }

  private static void RequireFfmpeg() {
    try {
      var result = Run("ffmpeg", "-version");
      if (result.ExitCode != 0)
        Assert.Ignore($"ffmpeg is not usable on PATH: {result.StdErr}");
    } catch (System.ComponentModel.Win32Exception) {
      Assert.Ignore("ffmpeg is not installed or not on PATH.");
    }
  }

  private static (int ExitCode, string StdOut, string StdErr) Run(string executable, params string[] arguments) {
    var start = new ProcessStartInfo(executable) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout, stderr);
  }
}
