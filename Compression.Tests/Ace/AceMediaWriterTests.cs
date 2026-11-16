using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Core.Dictionary.Ace;
using FileFormat.Ace;
using AceConstants = Compression.Core.Dictionary.Ace.AceConstants;

namespace Compression.Tests.Ace;

/// <summary>
/// The ACE 2.0 SOUND and PIC modes on the writer side: every SOUND variant and PIC plane layout
/// round-trips through our decoder, WAVE and BMP members pick them when they are smaller, and unace
/// 2.5 (in WSL) extracts what we wrote byte for byte.
/// </summary>
[TestFixture]
public sealed class AceMediaWriterTests {
  private static readonly DateTime FixtureTime = new(2024, 5, 6, 7, 8, 10);

  internal static byte[] Wave(int rate, int bits, int channels, int frames, int seed) {
    var r = new Random(seed);
    var data = new List<byte>();
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < channels; ++c) {
        var v = Math.Sin(2 * Math.PI * i * (220 + 110 * c) / rate) * 0.6 + Math.Sin(2 * Math.PI * i * 37 / rate) * 0.25 + (r.NextDouble() - 0.5) * 0.02;
        if (bits == 8)
          data.Add((byte)Math.Clamp((int)(128 + 127 * v), 0, 255));
        else {
          var s = (short)Math.Clamp((int)(32767 * v), short.MinValue, short.MaxValue);
          data.Add((byte)s);
          data.Add((byte)(s >> 8));
        }
      }
    var block = channels * bits / 8;
    var file = new List<byte>();
    void U16(int v) => file.AddRange([(byte)v, (byte)(v >> 8)]);
    void U32(int v) => file.AddRange([(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
    file.AddRange("RIFF"u8.ToArray());
    U32(4 + 8 + 16 + 8 + data.Count + (data.Count & 1));
    file.AddRange("WAVEfmt "u8.ToArray());
    U32(16);
    U16(1);
    U16(channels);
    U32(rate);
    U32(rate * block);
    U16(block);
    U16(bits);
    file.AddRange("data"u8.ToArray());
    U32(data.Count);
    file.AddRange(data);
    if ((data.Count & 1) != 0)
      file.Add(0);
    return [.. file];
  }

  internal static byte[] Bitmap(int width, int height, int bitsPerPixel, int seed, int trailer = 0) {
    var r = new Random(seed);
    var stride = (width * bitsPerPixel + 31) / 32 * 4;
    var rows = new byte[stride * height];
    for (var y = 0; y < height; ++y)
      for (var x = 0; x < width; ++x) {
        var at = y * stride + x * bitsPerPixel / 8;
        switch (bitsPerPixel) {
          case 8:
            rows[at] = (byte)(x * 3 + y * 2 + x * y / 64);
            break;
          case 16: {
            var v = (ushort)(((x * 31 / width) << 10) | ((y * 31 / height) << 5) | ((x + y + r.Next(2)) & 31));
            BinaryPrimitives.WriteUInt16LittleEndian(rows.AsSpan(at), v);
            break;
          }
          default:
            rows[at] = (byte)(x * 255 / Math.Max(1, width - 1) + r.Next(-2, 3));
            rows[at + 1] = (byte)(y * 255 / Math.Max(1, height - 1) + r.Next(-2, 3));
            rows[at + 2] = (byte)((x + y) * 2 + r.Next(-2, 3));
            if (bitsPerPixel == 32)
              rows[at + 3] = 0xFF;
            break;
        }
      }
    var palette = bitsPerPixel == 8 ? Enumerable.Range(0, 256).SelectMany(i => new[] { (byte)i, (byte)i, (byte)i, (byte)0 }).ToArray() : [];
    var offset = 14 + 40 + palette.Length;
    var file = new byte[offset + rows.Length + trailer];
    file[0] = (byte)'B';
    file[1] = (byte)'M';
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), offset);
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14), 40);
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), width);
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22), height);
    BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(26), 1);
    BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(28), (short)bitsPerPixel);
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(34), rows.Length);
    palette.CopyTo(file, 54);
    rows.CopyTo(file, offset);
    for (var i = 0; i < trailer; ++i)
      file[offset + rows.Length + i] = (byte)(i * 7);
    return file;
  }

  /// <summary>WAVE files of each sample layout (one with an odd length), BMPs of each depth (one with bytes after the rows).</summary>
  internal static (string Name, byte[] Data)[] Members() => [
    ("mono8.wav", Wave(11025, 8, 1, 30001, 1)),
    ("stereo8.wav", Wave(22050, 8, 2, 20000, 2)),
    ("mono16.wav", Wave(22050, 16, 1, 25000, 3)),
    ("stereo16.wav", Wave(44100, 16, 2, 20000, 4)),
    ("gray8.bmp", Bitmap(203, 120, 8, 5)),
    ("rgb16.bmp", Bitmap(77, 60, 16, 6)),
    ("rgb24.bmp", Bitmap(101, 90, 24, 7)),
    ("rgba32.bmp", Bitmap(96, 80, 32, 8)),
    ("trailer24.bmp", Bitmap(50, 40, 24, 9, trailer: 13)),
  ];

  internal static byte[] Archive(bool solid, string? forced = null, IEnumerable<(string Name, byte[] Data)>? members = null) {
    var w = new AceWriter(dictionaryBits: 16, solid: solid, ace20: true) { CreatedTime = FixtureTime, ForcedAce20Mode = forced };
    foreach (var (name, data) in members ?? Members())
      w.AddFile(name, data, FixtureTime);
    return w.ToArray();
  }

  private static byte[] RoundTrip(byte[] data, string layout) {
    var packed = new AceBlockedCompressor(16) { ForcedMode = layout }.Compress(data);
    var decoder = new AceDecompressor();
    var restored = decoder.Decompress(packed, data.Length, AceConstants.CompAce20, 16);
    Assert.That(decoder.LastModes, Does.Contain(int.Parse(layout.Split(':')[0] == "sound" ? layout.Split(':')[1] : "7")), layout);
    return restored;
  }

  private static byte[] Content(string kind, int size) {
    var r = new Random(size);
    return kind switch {
      "silence" => new byte[size],
      "constant" => Enumerable.Repeat((byte)0x80, size).ToArray(),
      "noise" => [.. Enumerable.Range(0, size).Select(_ => (byte)r.Next(256))],
      // stretches of silence (runs up to and beyond 32 per channel) between bursts of tone
      "bursts" => [.. Enumerable.Range(0, size).Select(i => (i / 97 % 3 == 0) ? (byte)(128 + 100 * Math.Sin(i * 0.3)) : (byte)0)],
      _ => [.. Enumerable.Range(0, size).Select(i => (byte)(128 + 90 * Math.Sin(i * 0.05) + r.Next(-3, 4)))],
    };
  }

  // Sizes around the four-byte rule: a multiple of four ends the member in SOUND mode; anything
  // else leaves the last four to seven bytes to LZ77. 70001 spans several 32767-symbol blocks.
  [Test]
  public void GivenEverySoundModeAndSize_WhenRoundTripped_ThenTheBytesAreRestored(
      [Values(3, 4, 5, 6)] int mode,
      [Values("silence", "constant", "noise", "bursts", "tone")] string kind,
      [Values(8, 9, 10, 11, 12, 64, 1001, 70001)] int size) {
    var data = Content(kind, size);
    Assert.That(RoundTrip(data, $"sound:{mode}"), Is.EqualTo(data));
  }

  [TestCase(4)]
  [TestCase(5)]
  [TestCase(7)]
  public void GivenTooFewBytesForASoundBlockAndAnLz77Tail_WhenForced_ThenTheLayoutIsRefused(int size) {
    var c = new AceBlockedCompressor(16) { ForcedMode = "sound:3" };
    if (size % 4 == 0)
      Assert.That(new AceDecompressor().Decompress(c.Compress(new byte[size]), size, AceConstants.CompAce20, 16), Is.EqualTo(new byte[size]));
    else
      Assert.Throws<ArgumentException>(() => c.Compress(new byte[size]));
  }

  // Width a multiple of the planes or not, one row or many, a remainder after the rows (LZ77 again).
  [TestCase(1, 1, 50)]
  [TestCase(7, 3, 70)]
  [TestCase(16, 4, 16)]
  [TestCase(16, 4, 17)]
  [TestCase(304, 3, 304 * 20 + 5)]
  [TestCase(4095, 2, 4095 * 3)]
  [TestCase(5000, 1, 5000 * 2 + 1)]
  public void GivenPicRows_WhenRoundTripped_ThenTheBytesAreRestored(int width, int planes, int size) {
    foreach (var kind in new[] { "noise", "tone", "silence" }) {
      var data = Content(kind, size);
      Assert.That(RoundTrip(data, $"pic:{width}:{planes}"), Is.EqualTo(data), kind);
    }
  }

  [TestCase("pic:10:1", 9)]
  [TestCase("pic:0:1", 100)]
  [TestCase("pic:10:0", 100)]
  [TestCase("sound:2", 100)]
  [TestCase("sound:7", 100)]
  [TestCase("rle", 100)]
  public void GivenALayoutThatDoesNotFit_WhenForced_ThenItIsRefused(string layout, int size)
    => Assert.Throws<ArgumentException>(() => new AceBlockedCompressor(16) { ForcedMode = layout }.Compress(new byte[size]));

  /// <summary>
  /// <c>pic-kind2.ace</c> holds a PIC member whose third plane uses decoder kind 2 on reference values
  /// of 128 and above (written before the writer stopped doing that). unace 2.5 extracts the bytes
  /// pinned here, which are not the member's input, so it reports a CRC error; the decoder has to read
  /// the stream as unace does.
  /// </summary>
  [Test]
  public void GivenPicKind2WithHighReferenceValues_WhenDecoded_ThenTheResultIsWhatUnaceExtracts() {
    using var stream = typeof(AceMediaWriterTests).Assembly.GetManifestResourceStream("AceVectors.pic-kind2.ace")!;
    var archive = new byte[stream.Length];
    stream.ReadExactly(archive);
    using var r = new AceReader(new MemoryStream(archive));
    var entry = r.Entries.Single();
    var (_, offset, length) = entry.Segments.Single();
    var decoded = new AceDecompressor().Decompress(archive.AsMemory((int)offset, (int)length), entry.OriginalSize, entry.CompressionType, entry.DictionaryBits);
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(decoded)), Is.EqualTo("1ee5f9a80ff0d25de21812ad059b883b6d4aeb58c6175a6642d8085420428fef"));
    Assert.That(decoded, Is.Not.EqualTo(Bitmap(50, 40, 24, 9, trailer: 13)), "the stream was written with the other reading");
  }

  [Test]
  public void GivenWaveAndBitmapMembers_WhenCompressed_ThenSoundAndPicAreChosenWhereSmaller() {
    var c = new AceBlockedCompressor(16);
    foreach (var (name, data) in Members()) {
      var packed = c.Compress(data);
      TestContext.Out.WriteLine($"{name}: {c.LastMode} {data.Length} -> {packed.Length}");
      if (name.EndsWith(".wav"))
        Assert.That(c.LastMode, Does.StartWith("sound:"), name);
    }
    c.Compress(Members().Single(m => m.Name == "rgb24.bmp").Data);
    Assert.That(c.LastMode, Is.EqualTo("pic"));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void GivenAMediaArchive_WhenReadBack_ThenEveryMemberIsRestoredThroughSoundAndPic(bool solid) {
    using var r = new AceReader(new MemoryStream(Archive(solid)));
    var modes = new HashSet<int>();
    foreach (var (name, data) in Members()) {
      Assert.That(r.ExtractEntry(r.Entries.Single(e => e.FileName == name)), Is.EqualTo(data), name);
      modes.UnionWith(r.LastModes);
    }
    Assert.That(modes, Does.Contain(AceConstants.ModePic));
    Assert.That(modes.Any(m => m is >= AceConstants.ModeSound8 and <= AceConstants.ModeSound32B), modes.Count.ToString());
  }

  /// <summary>Every layout unace has to take: the automatic choice (solid or not), each SOUND variant over every WAVE, and PIC with one to four planes.</summary>
  private static IEnumerable<TestCaseData> UnaceCases() {
    yield return new TestCaseData("auto", false);
    yield return new TestCaseData("auto-solid", true);
    for (var mode = 3; mode <= 6; ++mode)
      yield return new TestCaseData($"sound:{mode}", false);
    yield return new TestCaseData("pic:204:1", false);
    yield return new TestCaseData("pic:156:2", false);
    yield return new TestCaseData("pic:304:3", false);
    yield return new TestCaseData("pic:384:4", false);
  }

  private static (string Name, byte[] Data)[] MembersFor(string layout) => layout switch {
    _ when layout.StartsWith("sound:") => [.. Members().Where(m => m.Name.EndsWith(".wav"))],
    "pic:204:1" => [.. Members().Where(m => m.Name == "gray8.bmp")],
    "pic:156:2" => [.. Members().Where(m => m.Name == "rgb16.bmp")],
    "pic:304:3" => [.. Members().Where(m => m.Name == "rgb24.bmp")],
    "pic:384:4" => [.. Members().Where(m => m.Name == "rgba32.bmp")],
    _ => Members(),
  };

  [TestCaseSource(nameof(UnaceCases))]
  [Category("ExternalInterop")]
  public void GivenOurSoundAndPicArchive_WhenUnaceExtractsIt_ThenEveryMemberMatches(string layout, bool solid) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("unace"))
      Assert.Ignore("unace 2.5 not installed: `sudo apt-get install -y unace-nonfree`.");
    var members = MembersFor(layout);
    var archive = Archive(solid, layout.StartsWith("auto") ? null : layout, members);
    using (var r = new AceReader(new MemoryStream(archive)))
      foreach (var (name, data) in members)
        Assert.That(r.ExtractEntry(r.Entries.Single(e => e.FileName == name)), Is.EqualTo(data), name);

    var dir = Path.Combine(Path.GetTempPath(), "cwb-acemedia-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "media.ace");
      File.WriteAllBytes(path, archive);
      var run = FsInteropToolbox.RunWsl(
        $"d=$(mktemp -d) && cp {FsInteropToolbox.WinToWsl(path)} $d/ && cd $d && timeout 120 unace t -y media.ace >/dev/null && timeout 120 unace x -y media.ace >/dev/null && sha256sum *; r=$?; rm -rf $d; exit $r");
      TestContext.Out.WriteLine(run.StdOut + run.StdErr);
      Assert.That(run.ExitCode, Is.Zero, "unace refused our archive");
      foreach (var (name, data) in members)
        Assert.That(run.StdOut, Does.Contain(Convert.ToHexStringLower(SHA256.HashData(data)) + "  " + name), name);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
