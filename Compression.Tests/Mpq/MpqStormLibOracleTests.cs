using System.Diagnostics;
using System.Security.Cryptography;
using Compression.Core.Dictionary.PkwareDcl;
using FileFormat.Mpq;

namespace Compression.Tests.Mpq;

/// <summary>
/// StormLib as a live oracle, through the small <c>mpqoracle</c> front end in
/// <c>Mpq/ReferenceVectors/mpqoracle.c</c> (set <c>CWB_MPQ_ORACLE</c> to the built executable):
/// StormLib opens and extracts what our writer produces, and the codecs agree with StormLib's
/// in both directions. The writer cases are also pinned as committed vectors together with the
/// listing StormLib produced for them, so the proof keeps running where the tool is absent.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class MpqStormLibOracleTests {

  /// <summary>The writer profiles proven against StormLib; name → options.</summary>
  internal static readonly (string Name, MpqFileOptions Options)[] WriterProfiles = [
    ("ours-store", new MpqFileOptions()),
    ("ours-zlib", new MpqFileOptions(MpqCompression.Zlib)),
    ("ours-pkware", new MpqFileOptions(MpqCompression.Pkware)),
    ("ours-bzip2", new MpqFileOptions(MpqCompression.Bzip2)),
    ("ours-lzma", new MpqFileOptions(MpqCompression.Lzma)),
    ("ours-sparse-zlib", new MpqFileOptions((byte)(MpqCompression.Sparse | MpqCompression.Zlib))),
    ("ours-implode-encrypted", new MpqFileOptions(Implode: true, Encrypt: true)),
    ("ours-zlib-encrypted-v2key", new MpqFileOptions(MpqCompression.Zlib, Encrypt: true, AdjustedKey: true)),
  ];

  /// <summary>Deterministic inputs for the writer profiles.</summary>
  internal static IEnumerable<(string Name, byte[] Data)> WriterInputs() {
    var text = new byte[14_000];
    var words = "orcs and humans trade blows across the river at the crossing "u8.ToArray();
    for (var i = 0; i < text.Length; ++i)
      text[i] = words[(i * 5 + i / 11) % words.Length];
    var sparse = new byte[9000];
    for (var i = 0; i < sparse.Length; i += 512)
      sparse[i] = (byte)(i / 512 + 1);
    var noise = new byte[5000];
    new Random(42).NextBytes(noise);
    yield return ("war3map.j", text);
    yield return ("Units\\sparse.bin", sparse);
    yield return ("Data\\noise.bin", noise);
    yield return ("tiny.txt", "MPQ"u8.ToArray());
    yield return ("empty.bin", []);
  }

  internal static byte[] Write(MpqFileOptions options) {
    var writer = new MpqWriter(options);
    foreach (var (name, data) in WriterInputs())
      writer.AddFile(name, data);
    using var output = new MemoryStream();
    writer.WriteTo(output);
    return output.ToArray();
  }

  [TestCaseSource(nameof(ProfileNames))]
  public void Given_AWriterProfile_When_Written_Then_ItIsByteIdenticalToTheVectorStormLibAccepted(string profile) {
    var options = WriterProfiles.Single(p => p.Name == profile).Options;
    var written = Write(options);
    Assert.That(written, Is.EqualTo(MpqStormLibReferenceTests.Resource(profile + ".mpq")),
      "writer output changed; re-prove it with CWB_MPQ_ORACLE and CWB_MPQ_WRITE_VECTORS=1");

    var listing = ParseListing(System.Text.Encoding.UTF8.GetString(MpqStormLibReferenceTests.Resource(profile + ".stormlib.txt")));
    var inputs = WriterInputs().ToDictionary(i => i.Name, i => i.Data);
    foreach (var (name, data) in inputs) {
      Assert.That(listing, Does.ContainKey(name), name);
      Assert.That(listing[name].Status, Is.EqualTo("OK"), name);
      Assert.That(listing[name].Size, Is.EqualTo(data.Length), name);
    }
  }

  private static IEnumerable<string> ProfileNames() => WriterProfiles.Select(p => p.Name);

  [TestCaseSource(nameof(ProfileNames))]
  [Category("ExternalInterop")]
  public void Given_AWriterProfile_When_StormLibExtracts_Then_EveryFileMatches(string profile) {
    var tool = RequireOracle();
    var dir = Directory.CreateTempSubdirectory("cwb-mpq-").FullName;
    try {
      var archive = Path.Combine(dir, profile + ".mpq");
      var written = Write(WriterProfiles.Single(p => p.Name == profile).Options);
      File.WriteAllBytes(archive, written);
      var outDir = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
      var (stdout, stderr, code) = Run(tool, "extract", archive, outDir);
      Assert.That(code, Is.Zero, stderr + stdout);
      foreach (var (name, data) in WriterInputs()) {
        var extracted = File.ReadAllBytes(Path.Combine(outDir, name.Replace('\\', '#')));
        Assert.That(extracted, Is.EqualTo(data), name);
      }

      if (Environment.GetEnvironmentVariable("CWB_MPQ_WRITE_VECTORS") == "1") {
        var vectors = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Mpq", "ReferenceVectors");
        File.WriteAllBytes(Path.Combine(vectors, profile + ".mpq"), written);
        File.WriteAllText(Path.Combine(vectors, profile + ".stormlib.txt"), stdout.Replace("\r\n", "\n"));
      }
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [TestCase(PkwareDclMode.Binary, 1024)]
  [TestCase(PkwareDclMode.Binary, 2048)]
  [TestCase(PkwareDclMode.Binary, 4096)]
  [TestCase(PkwareDclMode.Ascii, 1024)]
  [TestCase(PkwareDclMode.Ascii, 2048)]
  [TestCase(PkwareDclMode.Ascii, 4096)]
  [Category("ExternalInterop")]
  public void Given_OurImplodedStream_When_StormLibExplodes_Then_TheBytesMatch(PkwareDclMode mode, int dictionary) {
    var data = WriterInputs().First().Data;
    var packed = PkwareDclEncoder.Compress(data, mode, dictionary);
    Assert.That(RoundTripThroughTool("explode", packed, data.Length), Is.EqualTo(data));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(700)]
  [TestCase(14_000)]
  [Category("ExternalInterop")]
  public void Given_StormLibsImplodedStream_When_WeExplode_Then_TheBytesMatch(int length) {
    var data = WriterInputs().First().Data[..length].Concat(new byte[length / 3]).ToArray();
    var packed = RoundTripThroughTool("implode", data, 0);
    Assert.That(PkwareDclDecoder.Decompress(packed, data.Length), Is.EqualTo(data));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(5)]
  [TestCase(6)]
  [TestCase(7)]
  [TestCase(8)]
  [Category("ExternalInterop")]
  public void Given_HuffmanOfEveryDataType_When_ExchangedWithStormLib_Then_BothDirectionsAgree(int dataType) {
    var data = WriterInputs().First().Data[..4000];
    // ours -> StormLib (mask 0x01 in a version-1 sector)
    var ours = MpqHuffman.Compress(data, dataType);
    // A generous output buffer: StormLib refuses a "compressed" sector that is not shorter than its buffer.
    Assert.That(RoundTripThroughTool("decomp", [MpqCompression.Huffman, .. ours], data.Length * 2 + 64), Is.EqualTo(data));
    // StormLib -> ours: the coding is fully determined by the data type, so the streams are identical
    // whenever StormLib keeps the compressed form (it stores the sector when Huffman does not help).
    var theirs = RoundTripThroughTool("comp", data, 0, $"{MpqCompression.Huffman:X2}", dataType.ToString(), "0");
    if (theirs.Length == data.Length) {
      Assert.That(ours.Length, Is.GreaterThan(data.Length - 2), "StormLib stored the sector, so Huffman must not have helped");
      return;
    }
    Assert.That(theirs[1..], Is.EqualTo(ours));
    Assert.That(MpqCompression.Decompress(theirs, data.Length, 0), Is.EqualTo(data));
  }

  [TestCase(MpqCompression.AdpcmMono)]
  [TestCase(MpqCompression.AdpcmStereo)]
  [Category("ExternalInterop")]
  public void Given_AdpcmSectors_When_ExchangedWithStormLib_Then_TheyAreIdenticalBothWays(byte adpcm) {
    var pcm = new byte[4096];
    for (var i = 0; i < pcm.Length / 2; ++i) {
      var value = (short)(9000 * Math.Sin(i / 9.0) + 2500 * Math.Sin(i / 2.3));
      pcm[2 * i] = (byte)value;
      pcm[2 * i + 1] = (byte)(value >> 8);
    }
    var mask = (byte)(adpcm | MpqCompression.Huffman);
    // StormLib level 4 = ADPCM at level 5 and Huffman type 7, which is what our encoder uses.
    var theirs = RoundTripThroughTool("comp", pcm, 0, $"{mask:X2}", "0", "4");
    var ours = MpqCompression.Compress(pcm, mask);
    Assert.That(ours, Is.EqualTo(theirs), "encoders differ");
    var theirDecode = RoundTripThroughTool("decomp", theirs, pcm.Length);
    Assert.That(MpqCompression.Decompress(theirs, pcm.Length, 0), Is.EqualTo(theirDecode));
  }

  [Test]
  [Category("ExternalInterop")]
  public void Given_Sparse_When_ExchangedWithStormLib_Then_BothDirectionsAgree() {
    var data = WriterInputs().Single(i => i.Name == "Units\\sparse.bin").Data[..4096];
    var ours = MpqCompression.Compress(data, MpqCompression.Sparse);
    Assert.That(ours[0], Is.EqualTo(MpqCompression.Sparse));
    Assert.That(RoundTripThroughTool("decomp", ours, data.Length), Is.EqualTo(data));
    var theirs = RoundTripThroughTool("comp", data, 0, $"{MpqCompression.Sparse:X2}", "0", "0");
    Assert.That(MpqCompression.Decompress(theirs, data.Length, 0), Is.EqualTo(data));
  }

  [Test]
  [Category("ExternalInterop")]
  public void Given_StormLibsLzmaSector_When_WeDecompress_Then_TheBytesMatch() {
    var data = WriterInputs().First().Data[..4096];
    var theirs = RoundTripThroughTool("comp", data, 0, $"{MpqCompression.Lzma:X2}", "0", "0");
    Assert.That(theirs[0], Is.EqualTo(MpqCompression.Lzma));
    Assert.That(MpqCompression.Decompress(theirs, data.Length, 1), Is.EqualTo(data));
  }

  /// <summary>Adds, replaces and removes files in place in StormLib's archive (which carries "(attributes)").</summary>
  internal static byte[] EditStormLibArchive() {
    var archive = MpqStormLibReferenceTests.Resource("stormlib-v1.mpq");
    using var stream = new MemoryStream();
    stream.Write(archive);
    var descriptor = new MpqFormatDescriptor();
    var dir = Directory.CreateTempSubdirectory("cwb-mpq-").FullName;
    try {
      var added = Path.Combine(dir, "added.txt");
      var replaced = Path.Combine(dir, "plain.txt");
      File.WriteAllBytes(added, "added in place"u8.ToArray());
      File.WriteAllBytes(replaced, "replaced in place"u8.ToArray());
      descriptor.Add(stream, [
        new Compression.Registry.ArchiveInputInfo(added, "Maps/added.txt", false),
        new Compression.Registry.ArchiveInputInfo(replaced, "plain.txt", false),
      ]);
      descriptor.Remove(stream, ["tiny.txt"]);
    } finally {
      Directory.Delete(dir, true);
    }
    return stream.ToArray();
  }

  [Test]
  public void Given_InPlaceEditsOfAStormLibArchive_When_ReadBack_Then_EveryFileStillPassesItsAttributes() {
    var edited = EditStormLibArchive();
    var files = MpqStormLibReferenceTests.ReadAll(edited); // throws if any (attributes) CRC is stale
    Assert.That(files, Does.Not.ContainKey("tiny.txt"));
    Assert.That(files, Does.ContainKey("(attributes)"), "the edit ran in place and kept (attributes)");
    Assert.That(files["Maps\\added.txt"], Is.EqualTo("added in place"u8.ToArray()));
    Assert.That(files["plain.txt"], Is.EqualTo("replaced in place"u8.ToArray()));
    var original = MpqStormLibReferenceTests.Manifest("stormlib-v1.manifest").Single(r => r.Name == "war3map.j");
    Assert.That(Sha256(files["war3map.j"]), Is.EqualTo(original.Sha256));
  }

  [Test]
  [Category("ExternalInterop")]
  public void Given_InPlaceEditsOfAStormLibArchive_When_StormLibVerifiesAndExtracts_Then_ItAgrees() {
    var tool = RequireOracle();
    var dir = Directory.CreateTempSubdirectory("cwb-mpq-").FullName;
    try {
      var archive = Path.Combine(dir, "edited.mpq");
      var edited = EditStormLibArchive();
      File.WriteAllBytes(archive, edited);

      var (verify, verifyErr, _) = Run(tool, "verify", archive);
      var lossy = new[] { "Sound\\stereo.wav", "Sound\\stereo-mid.wav", "Sound\\mono-adpcm.wav" };
      foreach (var line in verify.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
        var fields = line.TrimEnd('\r').Split('\t');
        var result = Convert.ToUInt32(fields[1], 16);
        const uint errors = 0x01 | 0x02 | 0x08 | 0x20 | 0x80; // open, read, sector CRC, CRC-32, MD5
        if (!lossy.Contains(fields[0]))
          Assert.That(result & errors, Is.Zero, $"{fields[0]}: {verify}{verifyErr}");
      }

      var outDir = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
      var (stdout, stderr, code) = Run(tool, "extract", archive, outDir);
      Assert.That(code, Is.Zero, stderr + stdout);
      foreach (var (name, data) in MpqStormLibReferenceTests.ReadAll(edited))
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, name.Replace('\\', '#'))), Is.EqualTo(data), name);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  /// <summary>Writes <paramref name="input"/>, runs the tool on it and returns what it wrote.</summary>
  private static byte[] RoundTripThroughTool(string command, byte[] input, int outputSize, params string[] leading) {
    var tool = RequireOracle();
    var dir = Directory.CreateTempSubdirectory("cwb-mpq-").FullName;
    try {
      var inPath = Path.Combine(dir, "in.bin");
      var outPath = Path.Combine(dir, "out.bin");
      File.WriteAllBytes(inPath, input);
      string[] args = command switch {
        "explode" or "decomp" => [command, outputSize.ToString(), inPath, outPath],
        "implode" => [command, inPath, outPath],
        _ => [command, .. leading, inPath, outPath],
      };
      var (stdout, stderr, code) = Run(tool, args);
      Assert.That(code, Is.Zero, $"mpqoracle {command} failed: {stderr}{stdout}");
      return File.ReadAllBytes(outPath);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  internal static string RequireOracle() {
    var tool = Environment.GetEnvironmentVariable("CWB_MPQ_ORACLE");
    if (string.IsNullOrEmpty(tool) || !File.Exists(tool))
      Assert.Ignore("Set CWB_MPQ_ORACLE to the mpqoracle executable built against StormLib (see Mpq/ReferenceVectors/README.md).");
    return tool!;
  }

  internal static (string Stdout, string Stderr, int Code) Run(string tool, params string[] args) {
    var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in args)
      info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (stdout.Result, stderr, process.ExitCode);
  }

  /// <summary>Parses mpqoracle's extract listing: name, size, flags, status.</summary>
  internal static Dictionary<string, (long Size, string Status)> ParseListing(string text)
    => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split('\t'))
      .Where(f => f.Length == 4)
      .ToDictionary(f => f[0], f => (long.Parse(f[1]), f[3]), StringComparer.OrdinalIgnoreCase);

  internal static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
