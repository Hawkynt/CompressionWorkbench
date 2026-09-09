#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

[TestFixture]
[Category("ExternalInterop")]
public sealed class AsfExternalInteropTests {
  private const int SampleRate = 8000;

  [TestCase(null, "pcm_s16le")]
  [TestCase("0x0006", "pcm_alaw")]
  [TestCase("0x0007", "pcm_mulaw")]
  public void OurWriter_IsRecognisedAndDecodedByFfmpeg(string? formatTag, string expectedCodec) {
    if (!OperatingSystem.IsLinux())
      Assert.Ignore("The ffmpeg ASF interoperability oracle runs on Linux.");
    if (Which("ffprobe") is null || Which("ffmpeg") is null)
      Assert.Ignore("ffmpeg/ffprobe are not installed.");

    var temp = Path.Combine(Path.GetTempPath(), $"cwb_asf_{Guid.NewGuid():N}.asf");
    try {
      var pcm = GeneratePcm();
      var wav = PcmCodec.ToWavBlob(pcm, 1, SampleRate, 16);
      var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };
      if (formatTag is not null)
        inputs.Add(ArchiveInputInfo.InMemory("streams/stream_01.info.txt",
          Encoding.UTF8.GetBytes($"format_tag = {formatTag}\n")));

      using (var output = File.Create(temp))
        new AsfFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

      var probe = Run("ffprobe",
        "-v", "error",
        "-select_streams", "a:0",
        "-show_entries", "stream=codec_name,sample_rate,channels",
        "-of", "default=noprint_wrappers=1",
        temp);
      Assert.That(probe.ExitCode, Is.Zero, $"ffprobe rejected our ASF:\n{probe.StdErr}\n{probe.StdOut}");
      Assert.Multiple(() => {
        Assert.That(probe.StdOut, Does.Contain($"codec_name={expectedCodec}"));
        Assert.That(probe.StdOut, Does.Contain($"sample_rate={SampleRate}"));
        Assert.That(probe.StdOut, Does.Contain("channels=1"));
      });

      var decode = Run("ffmpeg", "-hide_banner", "-v", "error", "-i", temp, "-f", "null", "-");
      Assert.That(decode.ExitCode, Is.Zero, $"ffmpeg could not decode our ASF:\n{decode.StdErr}\n{decode.StdOut}");
    } finally {
      try { File.Delete(temp); } catch { /* best effort */ }
    }
  }

  private static byte[] GeneratePcm() {
    var pcm = new byte[SampleRate * 2];
    for (var i = 0; i < SampleRate; ++i) {
      var sample = (short)(Math.Sin(i * (2.0 * Math.PI * 440.0 / SampleRate)) * 20_000);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sample);
    }
    return pcm;
  }

  private static string? Which(string tool) {
    var result = Run("which", tool);
    return result.ExitCode == 0 ? result.StdOut.Trim() : null;
  }

  private static (int ExitCode, string StdOut, string StdErr) Run(string fileName, params string[] arguments) {
    var start = new ProcessStartInfo(fileName) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout, stderr);
  }
}
