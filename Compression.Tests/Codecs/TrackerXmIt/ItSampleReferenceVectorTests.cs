#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Codec.TrackerXmIt;

namespace Compression.Tests.Codecs.TrackerXmIt;

/// <summary>Real Impulse Tracker modules with compressed samples; see ReferenceVectors/README.md.</summary>
internal static class ItReferenceVectors {

  public static readonly string[] Modules = ["tute.it", "1kb.it", "pms_are1.it", "chipdisko2.it", "what_if_i_took_you.it"];

  public static byte[] Load(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ItVectors." + name)
      ?? throw new FileNotFoundException("Embedded IT vector missing: " + name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>One compressed sample of a module, as its IMPS header describes it.</summary>
  public readonly record struct Sample(string Module, int Index, int HeaderOffset, int Length, bool Is16, bool It215, int DataPointer, int C5Speed);

  public static IEnumerable<Sample> CompressedSamples(string module, byte[] blob) {
    var orders = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(32));
    var instruments = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(34));
    var samples = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(36));
    var table = 192 + orders + instruments * 4;
    for (var i = 0; i < samples; ++i) {
      var header = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(table + i * 4));
      var flags = blob[header + 18];
      var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(header + 48));
      if ((flags & 0x09) != 0x09 || length == 0)
        continue;
      yield return new Sample(module, i, header, length, (flags & 0x02) != 0, (blob[header + 46] & 0x04) != 0,
        (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(header + 72)),
        (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(header + 60)));
    }
  }

  /// <summary>Decodes with the decoder under test and returns signed little-endian PCM bytes.</summary>
  public static byte[] DecodeToBytes(byte[] blob, in Sample s) {
    var stream = blob.AsSpan(s.DataPointer);
    if (!s.Is16)
      return Array.ConvertAll(ItSampleDecompressor.Decompress8(stream, s.Length, s.It215), v => unchecked((byte)v));
    var pcm = ItSampleDecompressor.Decompress16(stream, s.Length, s.It215);
    var bytes = new byte[pcm.Length * 2];
    for (var i = 0; i < pcm.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), pcm[i]);
    return bytes;
  }

  /// <summary>Byte length of the block sequence a sample's compressed data occupies.</summary>
  public static int CompressedSpan(byte[] blob, in Sample s, out List<int> blockStarts) {
    blockStarts = [];
    var perBlock = s.Is16 ? 0x4000 : 0x8000;
    var position = s.DataPointer;
    for (var done = 0; done < s.Length; done += perBlock) {
      blockStarts.Add(position - s.DataPointer);
      position += 2 + BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(position));
    }
    return position - s.DataPointer;
  }
}

/// <summary>
/// The decoder against real compressed samples written by Impulse Tracker 2.14 / 2.14v5 and by
/// OpenMPT 1.32, covering IT214 and IT215 packing at both sample widths and samples spanning up
/// to six blocks. Expected PCM digests were captured from libxmp 4.6.3 (MIT), a decoder
/// independent of this one, driven as a black box; see ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class ItSampleReferenceVectorTests {

  private static IEnumerable<TestCaseData> OracleDigests() {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ItVectors.libxmp-4.6.3.sha256.txt")
      ?? throw new FileNotFoundException("Embedded libxmp digest list missing");
    using var reader = new StreamReader(s);
    while (reader.ReadLine() is { } line) {
      var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      if (p.Length != 6) continue;
      yield return new TestCaseData(p[0], int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), p[4] == "it215", p[5])
        .SetName($"Libxmp_{p[0]}_{p[1]}_{p[3]}bit_{p[4]}");
    }
  }

  [Test, Category("HappyPath")]
  [TestCaseSource(nameof(OracleDigests))]
  public void GivenRealCompressedSample_WhenDecoded_ThenPcmMatchesLibxmpByteForByte(string module, int index, int length, int bits, bool it215, string sha256) {
    var blob = ItReferenceVectors.Load(module);
    var sample = ItReferenceVectors.CompressedSamples(module, blob).Single(s => s.Index == index);
    Assert.That((sample.Length, sample.Is16 ? 16 : 8, sample.It215), Is.EqualTo((length, bits, it215)));

    var pcm = ItReferenceVectors.DecodeToBytes(blob, sample);

    Assert.That(Convert.ToHexStringLower(SHA256.HashData(pcm)), Is.EqualTo(sha256));
  }

  [Test, Category("Spec")]
  public void GivenTheReferenceSet_ThenEveryFlavourAndMultiBlockSampleIsCovered() {
    var all = ItReferenceVectors.Modules
      .SelectMany(m => ItReferenceVectors.CompressedSamples(m, ItReferenceVectors.Load(m)))
      .ToList();
    var digests = OracleDigests().Select(t => ((string)t.Arguments[0]!, (int)t.Arguments[1]!)).ToHashSet();

    Assert.That(all.Select(s => (s.Module, s.Index)), Is.EquivalentTo(digests), "every compressed sample has a libxmp digest");
    foreach (var is16 in new[] { false, true })
      foreach (var it215 in new[] { false, true }) {
        var perBlock = is16 ? 0x4000 : 0x8000;
        Assert.That(all.Any(s => s.Is16 == is16 && s.It215 == it215 && s.Length <= perBlock), $"single-block {(is16 ? 16 : 8)}-bit it215={it215}");
        Assert.That(all.Any(s => s.Is16 == is16 && s.It215 == it215 && s.Length > perBlock), $"multi-block {(is16 ? 16 : 8)}-bit it215={it215}");
      }
  }

  [Test, Category("HappyPath")]
  public void GivenRealModule_WhenParsed_ThenEveryCompressedSampleCarriesItsFullDecodedPcm() {
    var blob = ItReferenceVectors.Load("what_if_i_took_you.it");

    var module = ItModule.Parse(blob);

    foreach (var s in ItReferenceVectors.CompressedSamples("what_if_i_took_you.it", blob)) {
      var expected = ItReferenceVectors.DecodeToBytes(blob, s);
      var pcm = module.Samples[s.Index].Pcm;
      Assert.That(pcm, Has.Length.EqualTo(s.Length), $"sample {s.Index}");
      if (s.Is16)
        Assert.That(pcm[^1], Is.EqualTo(BinaryPrimitives.ReadInt16LittleEndian(expected.AsSpan(expected.Length - 2))), $"sample {s.Index}");
      else
        Assert.That(pcm[^1], Is.EqualTo((short)(unchecked((sbyte)expected[^1]) << 8)), $"sample {s.Index}");
    }
  }

  private static IEnumerable<TestCaseData> MultiBlockSamples() {
    yield return new TestCaseData("pms_are1.it", 0).SetName("Truncated_pms_are1_0_8bit_it214_2blocks");
    yield return new TestCaseData("chipdisko2.it", 0).SetName("Truncated_chipdisko2_0_16bit_it214_6blocks");
    yield return new TestCaseData("what_if_i_took_you.it", 4).SetName("Truncated_what_if_4_8bit_it215_2blocks");
    yield return new TestCaseData("what_if_i_took_you.it", 8).SetName("Truncated_what_if_8_16bit_it215_6blocks");
  }

  [Test, Category("ErrorHandling")]
  [TestCaseSource(nameof(MultiBlockSamples))]
  public void GivenRealSampleCutShort_WhenDecoded_ThenRefusedInsteadOfInvented(string module, int index) {
    var blob = ItReferenceVectors.Load(module);
    var sample = ItReferenceVectors.CompressedSamples(module, blob).Single(s => s.Index == index);
    var span = ItReferenceVectors.CompressedSpan(blob, sample, out var blocks);
    var data = blob.AsSpan(sample.DataPointer, span).ToArray();
    Assert.That(blocks, Has.Count.GreaterThan(1));

    // The whole block sequence alone decodes (the decoder never needs bytes past it)…
    Assert.DoesNotThrow(() => DecodeOnly(data, sample));
    // …but losing the last byte, the last block, or the last block's header bytes is refused.
    Assert.Throws<InvalidDataException>(() => DecodeOnly(data[..^1], sample), "last byte missing");
    Assert.Throws<InvalidDataException>(() => DecodeOnly(data[..blocks[^1]], sample), "last block missing");
    Assert.Throws<InvalidDataException>(() => DecodeOnly(data[..(blocks[^1] + 1)], sample), "last header cut");
    Assert.Throws<InvalidDataException>(() => DecodeOnly(data[..(blocks[1] - 1)], sample), "first block cut");
  }

  [Test, Category("ErrorHandling")]
  public void GivenModuleCutInsideACompressedSample_WhenParsed_ThenThatSampleIsEmptyNotWrong() {
    var blob = ItReferenceVectors.Load("what_if_i_took_you.it");
    var last = ItReferenceVectors.CompressedSamples("what_if_i_took_you.it", blob).MaxBy(s => s.DataPointer);
    var span = ItReferenceVectors.CompressedSpan(blob, last, out _);
    var cut = blob[..(last.DataPointer + span / 2)];

    var module = ItModule.Parse(cut);

    Assert.That(module.Samples[last.Index].Pcm, Is.Empty);
  }

  private static void DecodeOnly(byte[] data, in ItReferenceVectors.Sample s) {
    if (s.Is16) ItSampleDecompressor.Decompress16(data, s.Length, s.It215);
    else ItSampleDecompressor.Decompress8(data, s.Length, s.It215);
  }
}

/// <summary>
/// libopenmpt (BSD-3-Clause, via ffmpeg's <c>libopenmpt</c> demuxer) as a second, independent
/// black-box oracle. For every real compressed sample a one-sample module is built twice — once
/// carrying the original compressed stream, once carrying our decoded PCM stored plain — and both
/// are rendered. Identical renders mean libopenmpt decoded the stream to exactly our PCM; a third
/// render with every PCM byte's low bit flipped must differ, proving the render sees the data.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
[Category("AudioExternalInterop")]
public sealed class ItSampleLibopenmptOracleTests {

  private static IEnumerable<TestCaseData> AllSamples()
    => ItReferenceVectors.Modules.SelectMany(m => ItReferenceVectors.CompressedSamples(m, ItReferenceVectors.Load(m))
      .Select(s => new TestCaseData(m, s.Index).SetName($"Libopenmpt_{m}_{s.Index}_{(s.Is16 ? 16 : 8)}bit_{(s.It215 ? "it215" : "it214")}")));

  [Test]
  [TestCaseSource(nameof(AllSamples))]
  public void GivenRealCompressedSample_WhenLibopenmptRendersItAndOurPcm_ThenRendersAreIdentical(string module, int index) {
    RequireFfmpegWithLibopenmpt();
    var blob = ItReferenceVectors.Load(module);
    var sample = ItReferenceVectors.CompressedSamples(module, blob).Single(s => s.Index == index);
    var span = ItReferenceVectors.CompressedSpan(blob, sample, out _);
    var pcm = ItReferenceVectors.DecodeToBytes(blob, sample);

    var theirs = Render(OneSampleModule(blob, sample, blob.AsSpan(sample.DataPointer, span), compressed: true));
    var ours = Render(OneSampleModule(blob, sample, pcm, compressed: false));
    var control = Render(OneSampleModule(blob, sample, pcm.Select(b => (byte)(b ^ 1)).ToArray(), compressed: false));

    Assert.That(control, Is.Not.EqualTo(theirs), "the render must depend on the sample data");
    Assert.That(ours, Is.EqualTo(theirs), "libopenmpt's decode of the compressed stream must equal our PCM");
  }

  /// <summary>
  /// A one-channel, sample-mode IT module that plays the sample once at C-5 (its C5 speed), with
  /// loops cleared and enough rows at speed 255 / tempo 32 (~19.9 s a row) for the whole sample.
  /// </summary>
  private static byte[] OneSampleModule(byte[] source, in ItReferenceVectors.Sample s, ReadOnlySpan<byte> data, bool compressed) {
    var header = source.AsSpan(s.HeaderOffset, 80).ToArray();
    header[17] = 64;                                            // global volume
    header[18] = (byte)(header[18] & (compressed ? 0x0F : 0x07)); // keep data/16-bit/(compressed); drop loops
    header[19] = 64;                                            // default volume
    header[47] = 32;                                            // default pan
    if (!compressed)
      header[46] = (byte)((header[46] | 0x01) & ~0x04);         // signed, not delta-coded

    var seconds = s.Length / (double)Math.Max(1, s.C5Speed);
    var rows = Math.Max(1, (int)Math.Ceiling((seconds + 1) / 19.9));
    var pattern = new byte[8 + 5 + (rows - 1)];
    BinaryPrimitives.WriteUInt16LittleEndian(pattern, (ushort)(pattern.Length - 8));
    BinaryPrimitives.WriteUInt16LittleEndian(pattern.AsSpan(2), (ushort)rows);
    pattern[8] = 0x81; pattern[9] = 0x03; pattern[10] = 60; pattern[11] = 1;  // ch1: note C-5, sample 1; rows end with 0

    var head = new byte[192];
    "IMPM"u8.CopyTo(head);
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(32), 2);       // orders
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(36), 1);       // samples
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(38), 1);       // patterns
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(40), 0x5132);  // cwt: a version string, no tracker guessing
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(42), 0x0214);
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(44), 0x09);    // stereo, linear slides
    head[48] = 128; head[49] = 48; head[50] = 255; head[51] = 32; head[52] = 128;
    for (var c = 0; c < 64; ++c) { head[64 + c] = (byte)(c == 0 ? 32 : 0xA0); head[128 + c] = 64; }

    const int sampleHeader = 192 + 2 + 8;
    const int patternOffset = sampleHeader + 80;
    var dataOffset = patternOffset + pattern.Length;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72), (uint)dataOffset);

    var module = new byte[dataOffset + data.Length];
    head.CopyTo(module, 0);
    module[192] = 0; module[193] = 255;
    BinaryPrimitives.WriteUInt32LittleEndian(module.AsSpan(194), sampleHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(module.AsSpan(198), patternOffset);
    header.CopyTo(module, sampleHeader);
    pattern.CopyTo(module, patternOffset);
    data.CopyTo(module.AsSpan(dataOffset));
    return module;
  }

  private static byte[] Render(byte[] module) {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_it_oracle_{Guid.NewGuid():N}.it");
    File.WriteAllBytes(path, module);
    try {
      var start = new ProcessStartInfo("ffmpeg") {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
      };
      foreach (var a in new[] { "-hide_banner", "-v", "error", "-f", "libopenmpt", "-i", path, "-f", "f32le", "pipe:1" })
        start.ArgumentList.Add(a);
      using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start");
      var stderr = process.StandardError.ReadToEndAsync();
      using var ms = new MemoryStream();
      process.StandardOutput.BaseStream.CopyTo(ms);
      process.WaitForExit();
      Assert.That(process.ExitCode, Is.Zero, stderr.Result);
      Assert.That(ms.Length, Is.GreaterThan(0), "ffmpeg rendered nothing");
      return SHA256.HashData(ms.ToArray());
    } finally {
      try { File.Delete(path); } catch { /* best effort */ }
    }
  }

  private static void RequireFfmpegWithLibopenmpt() {
    try {
      var start = new ProcessStartInfo("ffmpeg", "-hide_banner -demuxers") {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
      };
      using var process = Process.Start(start)!;
      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      if (process.ExitCode != 0 || !output.Contains("libopenmpt", StringComparison.Ordinal))
        Assert.Ignore("ffmpeg on PATH has no libopenmpt demuxer.");
    } catch (System.ComponentModel.Win32Exception) {
      Assert.Ignore("ffmpeg is not installed or not on PATH.");
    }
  }
}
