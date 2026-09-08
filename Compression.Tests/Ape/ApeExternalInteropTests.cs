#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Diagnostics;
using Compression.Registry;
using FileFormat.Ape;

namespace Compression.Tests.Ape;

[TestFixture]
[Category("ExternalInterop")]
public sealed class ApeExternalInteropTests {

  private const int SampleRate = 44_100;
  private string _temp = null!;

  private static IEnumerable<TestCaseData> Combinations() {
    foreach (var level in new[] { 1000, 2000, 3000, 4000, 5000 })
      foreach (var bits in new[] { 8, 16, 24 })
        foreach (var channels in new[] { 1, 2 })
          yield return new TestCaseData(level, bits, channels)
            .SetName($"Ffmpeg_{level}_{bits}bit_{channels}ch");
  }

  [SetUp]
  public void Setup() {
    this._temp = Path.Combine(Path.GetTempPath(), $"cwb_ape_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._temp);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._temp, recursive: true); } catch { /* best effort */ }
  }

  [TestCaseSource(nameof(Combinations))]
  public void FfmpegDecodesEveryImplementedEncoderCombinationByteExactly(int level, int bits, int channels) {
    RequireFfmpeg();
    var descriptor = new ApeFormatDescriptor();
    var pcm = GeneratePcm(bits, channels, frames: 256);
    var options = Options(level);

    using var encoded = new MemoryStream();
    descriptor.EncodePcm(
      encoded,
      new AudioPcmBuffer(
        new AudioPcmFormat(
          SampleRate,
          channels,
          bits,
          bits == 8 ? AudioPcmEncoding.UnsignedInteger : AudioPcmEncoding.SignedInteger),
        pcm),
      "ape",
      options);

    var apePath = Path.Combine(this._temp, "ours.ape");
    var rawPath = Path.Combine(this._temp, "ffmpeg.pcm");
    File.WriteAllBytes(apePath, encoded.ToArray());
    var (format, codec) = bits switch {
      8 => ("u8", "pcm_u8"),
      16 => ("s16le", "pcm_s16le"),
      24 => ("s24le", "pcm_s24le"),
      _ => throw new UnreachableException(),
    };

    RunFfmpeg(
      "-hide_banner", "-loglevel", "error", "-y",
      "-i", apePath,
      "-map", "0:a:0",
      "-f", format,
      "-acodec", codec,
      rawPath);

    Assert.That(File.ReadAllBytes(rawPath), Is.EqualTo(pcm),
      $"ffmpeg did not reproduce {level}/{bits}-bit/{channels}ch PCM byte-for-byte");
  }

  [Test]
  public void FfmpegDecodesPacketRebuiltContainer() {
    RequireFfmpeg();
    var descriptor = new ApeFormatDescriptor();
    const int frames = 73_728 + 123;
    var pcm = GeneratePcm(8, 1, frames);

    using var encoded = new MemoryStream();
    descriptor.EncodePcm(
      encoded,
      new AudioPcmBuffer(new AudioPcmFormat(SampleRate, 1, 8, AudioPcmEncoding.UnsignedInteger), pcm),
      "ape",
      Options(5000));

    encoded.Position = 0;
    Assert.That(descriptor.TryDemux(encoded, out var demuxed), Is.True);
    Assert.That(demuxed!.Packets, Has.Count.EqualTo(2));
    var shortened = demuxed with { Packets = demuxed.Packets.Take(1).ToArray() };

    using var rebuilt = new MemoryStream();
    descriptor.Mux(rebuilt, shortened, new FormatCreateOptions());
    var apePath = Path.Combine(this._temp, "rebuilt.ape");
    var rawPath = Path.Combine(this._temp, "rebuilt.pcm");
    File.WriteAllBytes(apePath, rebuilt.ToArray());

    RunFfmpeg(
      "-hide_banner", "-loglevel", "error", "-y",
      "-i", apePath,
      "-map", "0:a:0",
      "-f", "u8",
      "-acodec", "pcm_u8",
      rawPath);

    Assert.That(File.ReadAllBytes(rawPath), Is.EqualTo(pcm.AsSpan(0, 73_728).ToArray()));
  }

  private static FormatCreateOptions Options(int level) => new() {
    FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["CompressionLevel"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
    },
  };

  private static byte[] GeneratePcm(int bits, int channels, int frames) {
    var bytesPerSample = bits / 8;
    var pcm = new byte[checked(frames * channels * bytesPerSample)];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var position = (frame * channels + channel) * bytesPerSample;
        var seed = frame * 977 + channel * 811;
        switch (bits) {
          case 8:
            pcm[position] = (byte)(128 + ((seed % 181) - 90));
            break;
          case 16:
            BinaryPrimitives.WriteInt16LittleEndian(
              pcm.AsSpan(position, 2), (short)(((seed * 37) % 60_001) - 30_000));
            break;
          case 24: {
            var value = ((seed * 65_537) % 12_000_001) - 6_000_000;
            pcm[position] = (byte)value;
            pcm[position + 1] = (byte)(value >> 8);
            pcm[position + 2] = (byte)(value >> 16);
            break;
          }
        }
      }
    return pcm;
  }

  private static void RequireFfmpeg() {
    if (FindOnPath("ffmpeg") is null)
      Assert.Ignore("ffmpeg is not installed on PATH.");
  }

  private static void RunFfmpeg(params string[] arguments) {
    var ffmpeg = FindOnPath("ffmpeg") ?? throw new InvalidOperationException("ffmpeg disappeared from PATH.");
    var start = new ProcessStartInfo {
      FileName = ffmpeg,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffmpeg.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    if (!process.WaitForExit(60_000)) {
      process.Kill(entireProcessTree: true);
      Assert.Fail("ffmpeg timed out.");
    }
    Assert.That(process.ExitCode, Is.Zero, $"ffmpeg failed.\nstdout: {stdout}\nstderr: {stderr}");
  }

  private static string? FindOnPath(string tool) {
    var extensions = OperatingSystem.IsWindows()
      ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD").Split(';')
      : [""];
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)) {
      if (string.IsNullOrWhiteSpace(directory))
        continue;
      foreach (var extension in extensions) {
        try {
          var candidate = Path.Combine(directory, tool + extension);
          if (File.Exists(candidate))
            return candidate;
        } catch {
          // Ignore malformed PATH elements.
        }
      }
    }
    return null;
  }
}
