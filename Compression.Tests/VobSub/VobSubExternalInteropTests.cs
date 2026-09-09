#pragma warning disable CS1591
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using FileFormat.VobSub;

namespace Compression.Tests.VobSub;

[TestFixture]
[Category("ExternalInterop")]
public sealed class VobSubExternalInteropTests {
  private const string IndexTemplate =
    "# VobSub index file, v7\n" +
    "size: 720x480\n" +
    "palette: 000000, ffffff, ff0000, 00ff00, 0000ff, ffff00, 00ffff, ff00ff, 808080, c0c0c0, 800000, 008000, 000080, 808000, 008080, 800080\n" +
    "id: en, index: 0\n" +
    "timestamp: 00:00:01:000, filepos: 0000000000\n" +
    "timestamp: 00:00:02:000, filepos: 0000000000\n";

  [Test]
  public void Ffprobe_DemuxesManagedWriterOutput() {
    var rawA = RawSpu(0x11);
    var rawB = RawSpu(0x22);
    var pair = VobSubWriter.Build(IndexTemplate, [
      new VobSubWriter.Frame(rawA, VobSubWriter.FrameKind.RawSpu),
      new VobSubWriter.Frame(rawB, VobSubWriter.FrameKind.RawSpu),
    ]);

    var directory = Path.Combine(Path.GetTempPath(), "cwb_vobsub_ffprobe_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var idx = Path.Combine(directory, "sample.idx");
      File.WriteAllBytes(idx, pair.IndexBytes);
      File.WriteAllBytes(Path.Combine(directory, "sample.sub"), pair.SubBytes);

      var result = RunFfprobe(idx);
      Assert.Multiple(() => {
        Assert.That(result.ExitCode, Is.Zero, result.StandardError);
        Assert.That(result.StandardOutput, Is.Not.Empty,
          "ffprobe accepted the pair but exposed no VobSub subtitle packets.");
      });
    } finally {
      try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
    }
  }

  private static byte[] RawSpu(byte data) => [
    0x00, 0x0A,
    0x00, 0x05,
    data,
    0x00, 0x00,
    0x00, 0x05,
    0xFF,
  ];

  private static (int ExitCode, string StandardOutput, string StandardError) RunFfprobe(string idxPath) {
    var start = new ProcessStartInfo {
      FileName = "ffprobe",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    start.ArgumentList.Add("-v");
    start.ArgumentList.Add("error");
    start.ArgumentList.Add("-select_streams");
    start.ArgumentList.Add("s:0");
    start.ArgumentList.Add("-show_entries");
    start.ArgumentList.Add("packet=pts_time,size");
    start.ArgumentList.Add("-of");
    start.ArgumentList.Add("csv=p=0");
    start.ArgumentList.Add(idxPath);

    Process process;
    try {
      process = Process.Start(start) ?? throw new InvalidOperationException("ffprobe did not start.");
    } catch (Win32Exception) {
      Assert.Ignore("ffprobe is not installed or is not on PATH.");
      return default;
    }

    using (process) {
      var stdout = process.StandardOutput.ReadToEnd();
      var stderr = process.StandardError.ReadToEnd();
      process.WaitForExit();
      return (process.ExitCode, stdout, stderr);
    }
  }
}
