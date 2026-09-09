#pragma warning disable CS1591
using System.Diagnostics;
using Compression.Registry;
using FileFormat.Avi;

namespace Compression.Tests.Avi;

/// <summary>
/// Proves the AVI muxer against ffmpeg rather than against our own reader: ffmpeg
/// authors the source file, our descriptor remuxes it, and ffmpeg decodes both.
/// A remux that only satisfied our own parser would pass the in-process tests and
/// fail here.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class AviExternalInteropTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_avi_interop_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  [Test]
  public void OurRemux_OfAnFfmpegAuthoredAvi_DecodesIdenticallyInFfmpeg() {
    RequireFfmpeg();

    var sourcePath = Path.Combine(this._tmpDir, "source.avi");
    var authored = Run("ffmpeg", "-hide_banner", "-v", "error", "-y",
      "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10:duration=2",
      "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
      "-c:v", "mpeg4", "-c:a", "pcm_s16le", "-ar", "8000", "-ac", "1", sourcePath);
    Assert.That(authored.ExitCode, Is.Zero, authored.StdErr);

    var source = File.ReadAllBytes(sourcePath);
    using var remuxed = new MemoryStream();
    ((IArchiveCreatable)new AviFormatDescriptor()).Create(
      remuxed,
      [ArchiveInputInfo.InMemory("FULL.avi", source)],
      new FormatCreateOptions());

    var remuxedPath = Path.Combine(this._tmpDir, "remuxed.avi");
    File.WriteAllBytes(remuxedPath, remuxed.ToArray());

    Assert.Multiple(() => {
      Assert.That(ExtractStream(sourcePath, "0:a", "s16le", "audio-source"),
        Is.EqualTo(ExtractStream(remuxedPath, "0:a", "s16le", "audio-remuxed")),
        "ffmpeg must decode the same audio samples from the remux.");
      Assert.That(ExtractStream(sourcePath, "0:v", "rawvideo", "video-source"),
        Is.EqualTo(ExtractStream(remuxedPath, "0:v", "rawvideo", "video-remuxed")),
        "ffmpeg must decode the same video frames from the remux.");
    });
  }

  private byte[] ExtractStream(string path, string map, string format, string name) {
    var outputPath = Path.Combine(this._tmpDir, $"{name}.raw");
    var result = Run("ffmpeg", "-hide_banner", "-v", "error", "-y",
      "-i", path, "-map", map, "-f", format, outputPath);
    Assert.That(result.ExitCode, Is.Zero, result.StdErr);
    return File.ReadAllBytes(outputPath);
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
