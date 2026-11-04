#pragma warning disable CS1591
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Codec.Nes2a03;

namespace Compression.Tests.Codecs.Nes2a03;

/// <summary>
/// Compares FDS renders with Game Music Emu (ffmpeg's libgme demuxer), used only as a black-box
/// oracle, on three homebrew test NSFs (see ReferenceVectors/README.md).
/// </summary>
/// <remarks>
/// <para>The two renderers resample and filter differently (libgme band-limits and applies its own
/// equaliser; NsfPlayer box-averages the CPU-rate signal), so samples are not compared. Per analysis
/// window (4096-sample Hann, hop 2205 at 44.1 kHz) the test compares (a) the RMS contour, each
/// render normalised to its own loudest window, and (b) the cosine similarity of the FFT magnitude
/// spectra between 11 Hz and 5 kHz. Each window may match its oracle neighbour one hop (50 ms)
/// either side, which absorbs the renderers' different start-up timing.</para>
/// <para>Tolerances were set from measurement (2026-10-30, ffmpeg 8.1.2 libgme) of this
/// implementation and of the implementation it replaced against the oracle:</para>
/// <list type="table">
///   <listheader><term>render</term><description>worst loudness Δ / min similarity / mean similarity</description></listheader>
///   <item><term>this, tone</term><description>0.109 / 0.978 / 0.998 (Δ from libgme's bass roll-off at 109 Hz)</description></item>
///   <item><term>this, envelope</term><description>0.155 / 0.757 / 0.995 (worst windows straddle note on/off edges)</description></item>
///   <item><term>this, modulation</term><description>0.042 / 0.863 / 0.993</description></item>
///   <item><term>previous, envelope</term><description>0.690 / 0.533 / 0.991 (treated <c>$4083</c> bit 6 differently)</description></item>
///   <item><term>previous, modulation</term><description>0.043 / 0.199 / 0.420 (one table slot per <c>$4088</c> write)</description></item>
/// </list>
/// <para>The limits below (0.2 / 0.7 / 0.98) sit between those groups. One stretch is excluded:
/// in fds_modulation.nsf from 5.0 to 6.05 s the modulator is halted with a non-zero counter and
/// gain. The NESdev wiki states the pitch-modulation formula stays in effect while the modulator
/// is halted ($4087 bit 7), and this implementation follows it; libgme plays the unmodulated
/// pitch there. That stretch is checked against the formula instead, by the unit tests.</para>
/// </remarks>
[TestFixture]
public class FdsAudioOracleTests {

  private const int Rate = 44100;
  private const double Seconds = 12;
  private const int Window = 4096;
  private const int Hop = 2205;
  private const int LowBin = 20 * Window / Rate;     // 1
  private const int HighBin = 5000 * Window / Rate;  // 464 (exclusive)
  private const int Bins = HighBin - LowBin;

  private const double SilenceFraction = 0.1;
  private const double LoudnessTolerance = 0.2;
  private const double MinimumSimilarity = 0.7;
  private const double MinimumMeanSimilarity = 0.98;

  internal sealed record Frame(double Time, double Rms, double[] Spectrum);

  public static readonly string[] Tunes = ["fds_tone", "fds_envelope", "fds_modulation"];

  private static readonly Dictionary<string, (double From, double To)[]> Excluded = new() {
    ["fds_modulation"] = [(5.0, 6.05)],
  };

  [TestCaseSource(nameof(Tunes))]
  public void GivenHomebrewFdsNsf_WhenRendered_ThenItMatchesTheCapturedLibgmeRender(string tune)
    => AssertAgree(tune, Analyse(Render(tune)), ReadCapture(tune));

  [TestCaseSource(nameof(Tunes))]
  [Category("ExternalInterop")]
  [Category("AudioExternalInterop")]
  public void GivenHomebrewFdsNsf_WhenRenderedByLibgme_ThenItMatchesOurRender(string tune) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_fds_oracle_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var nsf = Path.Combine(dir, tune + ".nsf");
      var raw = Path.Combine(dir, tune + ".raw");
      File.WriteAllBytes(nsf, Vector(tune + ".nsf"));
      var (exit, error) = RunFfmpeg("-hide_banner", "-v", "error", "-y", "-sample_rate", Rate.ToString(CultureInfo.InvariantCulture),
        "-i", nsf, "-t", Seconds.ToString(CultureInfo.InvariantCulture), "-ac", "1", "-f", "s16le", "-acodec", "pcm_s16le", raw);
      if (exit != 0)
        Assert.Ignore($"ffmpeg with libgme could not render the NSF: {error}");
      var bytes = File.ReadAllBytes(raw);
      var pcm = new short[bytes.Length / 2];
      Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
      AssertAgree(tune, Analyse(Render(tune)), Analyse(pcm));
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
  }

  private static void AssertAgree(string tune, IReadOnlyList<Frame> ours, IReadOnlyList<Frame> oracle) {
    var count = Math.Min(ours.Count, oracle.Count);
    Assert.That(count, Is.GreaterThan(100), "too few windows to compare");
    var oursPeak = ours.Take(count).Max(f => f.Rms);
    var oraclePeak = oracle.Take(count).Max(f => f.Rms);
    var excluded = Excluded.GetValueOrDefault(tune, []);

    var failures = new List<string>();
    var similarities = new List<double>();
    for (var i = 0; i < count; ++i) {
      var time = ours[i].Time;
      if (excluded.Any(e => time >= e.From && time <= e.To))
        continue;

      var neighbours = Enumerable.Range(i - 1, 3).Where(j => j >= 0 && j < count).ToArray();
      var loudness = ours[i].Rms / oursPeak;
      var loudnessError = neighbours.Min(j => Math.Abs(loudness - oracle[j].Rms / oraclePeak));
      if (loudnessError > LoudnessTolerance)
        failures.Add($"t={time:0.000}s loudness off by {loudnessError:0.000}");

      if (Math.Min(loudness, oracle[i].Rms / oraclePeak) < SilenceFraction)
        continue;
      var similarity = neighbours.Max(j => Cosine(ours[i].Spectrum, oracle[j].Spectrum));
      similarities.Add(similarity);
      if (similarity < MinimumSimilarity)
        failures.Add($"t={time:0.000}s spectral similarity {similarity:0.000}");
    }

    Assert.That(similarities, Has.Count.GreaterThan(count / 2), "most windows should carry sound");
    Assert.Multiple(() => {
      Assert.That(failures, Is.Empty);
      Assert.That(similarities.Average(), Is.GreaterThanOrEqualTo(MinimumMeanSimilarity), "mean spectral similarity");
    });
  }

  private static double Cosine(double[] a, double[] b) {
    double dot = 0, na = 0, nb = 0;
    for (var k = 0; k < a.Length; ++k) {
      dot += a[k] * b[k];
      na += a[k] * a[k];
      nb += b[k] * b[k];
    }
    return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
  }

  private static short[] Render(string tune) => NsfPlayer.FromNesm(Vector(tune + ".nsf")).Render(Seconds, Rate);

  /// <summary>Per window: RMS and FFT magnitudes of the mean-removed, Hann-windowed samples.</summary>
  internal static List<Frame> Analyse(short[] pcm) {
    var hann = new double[Window];
    for (var i = 0; i < Window; ++i)
      hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1));

    var result = new List<Frame>();
    var buffer = new Complex[Window];
    for (var start = 0; start + Window < pcm.Length; start += Hop) {
      var mean = 0.0;
      for (var i = 0; i < Window; ++i)
        mean += pcm[start + i];
      mean /= Window;

      var energy = 0.0;
      for (var i = 0; i < Window; ++i) {
        var v = pcm[start + i] - mean;
        energy += v * v;
        buffer[i] = new Complex(v * hann[i], 0);
      }

      Fft(buffer);
      var spectrum = new double[Bins];
      for (var k = 0; k < Bins; ++k)
        spectrum[k] = buffer[LowBin + k].Magnitude;
      result.Add(new((start + Window / 2.0) / Rate, Math.Sqrt(energy / Window), spectrum));
    }
    return result;
  }

  private static void Fft(Complex[] data) {
    var n = data.Length;
    for (int i = 1, j = 0; i < n; ++i) {
      var bit = n >> 1;
      for (; (j & bit) != 0; bit >>= 1)
        j ^= bit;
      j ^= bit;
      if (i < j)
        (data[i], data[j]) = (data[j], data[i]);
    }
    for (var length = 2; length <= n; length <<= 1) {
      var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
      for (var i = 0; i < n; i += length) {
        var w = Complex.One;
        for (var k = 0; k < length / 2; ++k) {
          var u = data[i + k];
          var v = data[i + k + length / 2] * w;
          data[i + k] = u + v;
          data[i + k + length / 2] = u - v;
          w *= step;
        }
      }
    }
  }

  /// <summary>The libgme features captured by ReferenceVectors/capture_gme_features.py.</summary>
  private static List<Frame> ReadCapture(string tune) {
    var spectra = Vector(tune + ".gme.spec");
    using var reader = new StreamReader(new MemoryStream(Vector(tune + ".gme.tsv")));
    var result = new List<Frame>();
    while (reader.ReadLine() is { } line) {
      if (line.Length == 0 || line[0] == '#')
        continue;
      var parts = line.Split('\t');
      var offset = result.Count * Bins;
      var spectrum = new double[Bins];
      for (var k = 0; k < Bins; ++k)
        spectrum[k] = spectra[offset + k];
      result.Add(new(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture), spectrum));
    }
    Assert.That(spectra.Length, Is.EqualTo(result.Count * Bins), "spectrum capture matches the window list");
    return result;
  }

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FdsVectors." + name)
      ?? throw new InvalidOperationException($"missing embedded vector {name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static (int ExitCode, string StdErr) RunFfmpeg(params string[] arguments) {
    var start = new ProcessStartInfo("ffmpeg") {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);
    try {
      using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start ffmpeg");
      var stdout = process.StandardOutput.ReadToEndAsync();
      var stderr = process.StandardError.ReadToEnd();
      process.WaitForExit();
      _ = stdout.Result;
      return (process.ExitCode, stderr);
    } catch (System.ComponentModel.Win32Exception) {
      Assert.Ignore("ffmpeg is not installed or not on PATH.");
      throw;
    }
  }
}
