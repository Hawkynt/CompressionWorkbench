using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Entropy.Ppmd;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// ZIP method 98 (PPMd var.I rev.1) against 7-Zip: archives 7-Zip wrote decode byte for byte, and
/// 7-Zip extracts what we write, across orders, memory sizes and both restore methods.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class ZipPpmdOracleTests {
  // Written by 7-Zip 26.04: `7z a -tzip -mm=PPMd` with -mo=6 / -mo=16 -mmem=1m / -mx=9 -mo=2 -mmem=1m.
  private static readonly (string Name, string Sha256)[] FixtureEntries = [
    ("text-o6.txt", "a077de736ad001491c3e872669ccabc0039cb73c6fe9ab61d87efedfda1f566f"),
    ("mixed-o16.bin", "9f163a1d15f9275c7e3050484f5ec570ffb4f656091c65d8c70c261a277742a6"),
    ("text-o2-cutoff.txt", "fe10e7a29f7b662a76eb556eb97d8e1eeb3f5a8933d8007b6fc8376b896ea8e9"),
  ];

  [Test]
  public void Given_7ZipWrittenPpmdEntries_When_Extracted_Then_TheyMatchTheOriginals() {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZipPpmdVectors.7zip-ppmd.zip")!;
    using var reader = new ZipReader(stream);

    foreach (var (name, sha256) in FixtureEntries) {
      var entry = reader.Entries.Single(e => e.FileName == name);
      Assert.That(entry.CompressionMethod, Is.EqualTo(ZipCompressionMethod.Ppmd), name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.ExtractEntry(entry))), Is.EqualTo(sha256), name);
    }
  }

  [TestCase("-mx=5 -mmem=16m -mo=6", 400_000)]
  [TestCase("-mx=5 -mmem=1m -mo=2", 3_000_000)]
  [TestCase("-mx=5 -mmem=1m -mo=8", 3_000_000)]
  [TestCase("-mx=9 -mmem=1m -mo=16", 3_000_000)]
  [Category("ExternalInterop")]
  public void Given_7ZipCompressesWithPpmd_When_WeExtract_Then_TheBytesMatch(string switches, int size) {
    FsInteropToolbox.Require7z();
    var dir = NewTempDir();
    try {
      var text = Text(size);
      var mixed = Mixed(size / 8);
      File.WriteAllBytes(Path.Combine(dir, "text.txt"), text);
      File.WriteAllBytes(Path.Combine(dir, "mixed.bin"), mixed);
      var archive = Path.Combine(dir, "a.zip");
      var (_, err, code) = FsInteropToolbox.Run7z($"a -tzip -mm=PPMd {switches} \"{archive}\" \"{Path.Combine(dir, "text.txt")}\" \"{Path.Combine(dir, "mixed.bin")}\"");
      Assert.That(code, Is.Zero, err);

      using var reader = new ZipReader(File.OpenRead(archive));
      Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.FileName == "text.txt")), Is.EqualTo(text));
      Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.FileName == "mixed.bin")), Is.EqualTo(mixed));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [TestCase(2, 1, Ppmd8RestoreMethod.Restart, 2_000_000)]
  [TestCase(6, 8, Ppmd8RestoreMethod.Restart, 300_000)]
  [TestCase(8, 1, Ppmd8RestoreMethod.Restart, 2_000_000)]
  [TestCase(16, 1, Ppmd8RestoreMethod.CutOff, 2_000_000)]
  [TestCase(4, 1, Ppmd8RestoreMethod.CutOff, 2_000_000)]
  [TestCase(6, 1, Ppmd8RestoreMethod.Restart, 0)]
  [TestCase(6, 1, Ppmd8RestoreMethod.Restart, 1)]
  [Category("ExternalInterop")]
  public void Given_OurPpmdEntries_When_7ZipExtracts_Then_TheBytesMatch(int order, int memoryMb, Ppmd8RestoreMethod restore, int size) {
    FsInteropToolbox.Require7z();
    var dir = NewTempDir();
    try {
      var text = Text(size);
      var mixed = Mixed(size / 8);
      var archive = Path.Combine(dir, "ours.zip");
      using (var file = File.Create(archive))
      using (var writer = new ZipWriter(file)) {
        writer.AddRawEntry("text.txt", ZipPpmdHelper.Compress(text, order, memoryMb, restore), ZipCompressionMethod.Ppmd,
          Compression.Core.Checksums.Crc32.Compute(text), text.Length);
        writer.AddRawEntry("mixed.bin", ZipPpmdHelper.Compress(mixed, order, memoryMb, restore), ZipCompressionMethod.Ppmd,
          Compression.Core.Checksums.Crc32.Compute(mixed), mixed.Length);
        writer.Finish();
      }

      var outDir = Path.Combine(dir, "out");
      var (stdout, err, code) = FsInteropToolbox.Run7z($"x -y \"-o{outDir}\" \"{archive}\"");
      Assert.That(code, Is.Zero, stdout + err);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "text.txt")), Is.EqualTo(text));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "mixed.bin")), Is.EqualTo(mixed));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_PpmdDataCutShort_When_Extracted_Then_ItThrows() {
    var data = Text(50_000);
    var packed = ZipPpmdHelper.Compress(data);
    Assert.That(() => ZipPpmdHelper.Decompress(packed[..(packed.Length / 2)], data.Length), Throws.InstanceOf<InvalidDataException>());
  }

  [TestCase(0x0000, typeof(InvalidDataException))] // order 1
  [TestCase(0x3005, typeof(InvalidDataException))] // restore method 3
  [TestCase(0x2005, typeof(NotSupportedException))] // restore method 2 (freeze)
  public void Given_InvalidProperties_When_Extracted_Then_ItRefuses(int properties, Type expected) {
    byte[] packed = [(byte)properties, (byte)(properties >> 8), 0, 0, 0, 0, 0, 0];
    Assert.That(() => ZipPpmdHelper.Decompress(packed, 4), Throws.InstanceOf(expected));
  }

  [Test]
  public void Given_OurEncoder_When_DataRoundTrips_Then_TheEndMarkerFollowsTheLastSymbol() {
    var data = Text(10_000);
    var packed = ZipPpmdHelper.Compress(data, 6, 1);
    var model = new Ppmd8(1 << 20, 6, Ppmd8RestoreMethod.Restart);
    Assert.That(model.StartDecoding(packed, 2), Is.True);
    for (var i = 0; i < data.Length; ++i)
      Assert.That(model.DecodeSymbol(), Is.EqualTo(data[i]));

    Assert.That(model.DecodeSymbol(), Is.EqualTo(Ppmd8.EndMarker));
    Assert.That(model.DecoderFinishedOk, Is.True);
  }

  private static string NewTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-zip-ppmd-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static byte[] Text(int size) {
    string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "model", "context", "order", "escape", "symbol", "range"];
    var state = 0x2545F491u;
    var sb = new StringBuilder(size + 64);
    for (var line = 0; sb.Length < size; ++line) {
      var count = 3 + (int)(Next(ref state) % 12);
      for (var w = 0; w < count; ++w)
        sb.Append(words[Next(ref state) % (uint)words.Length]).Append(' ');
      sb.Append(line * 7919 % 100003).Append('\n');
    }

    return Encoding.ASCII.GetBytes(sb.ToString(0, size));
  }

  private static byte[] Mixed(int size) {
    var state = 0x9E3779B9u;
    var b = new byte[size];
    for (var i = 0; i < size; ++i)
      b[i] = (i % 7) switch {
        < 3 => (byte)Next(ref state),
        < 5 => (byte)(i * 31),
        _ => i == 0 ? (byte)0 : b[i - 1 - (int)(Next(ref state) % (uint)Math.Min(i, 5000))],
      };

    return b;
  }

  private static uint Next(ref uint state) {
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    return state;
  }
}
