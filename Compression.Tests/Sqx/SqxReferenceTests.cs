using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Core.Dictionary.Sqx;
using Compression.Tests.CodecProof;
using FileFormat.Sqx;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Sqx;

/// <summary>
/// Third-party proof for SQX. The archives in <c>ReferenceVectors</c> come from Squeez-c's
/// <c>packed_corpus</c> (https://github.com/seb3773/Squeez-c, MIT), its set of reference SQX
/// archives in formats 1.1 and 2.0 at every LZ level; each must extract to the original file,
/// identified here by size and SHA-256. Where Squeez-c's <c>sqx</c> tool is on the PATH or named
/// by <c>CWB_TOOL_SQX</c>, it extracts and checks every archive we write.
/// </summary>
[TestFixture]
public sealed class SqxReferenceTests {

  private static readonly Dictionary<string, (long Size, string Sha256)> Originals = new() {
    ["hello.txt"] = (63, "b16f492254e9088104294aeeac03ae98b60bd6b651ec0e160dd4fe5c2296ca23"),
    ["code.c"] = (45270, "8434a94656f53eeab4dd357f998dec9f174dde9e6dc114392c40c5586610d51d"),
    ["eng.txt"] = (20359, "2ad832cec2c14f0fe6d6aa4fd9f9aefbfc4ae497f8b16fbe9c0fc6e354219786"),
    ["image.raw"] = (65536, "0cb91ad56c3968270986c9057f64573c5e011a5523ac18d1871aebff3931d5a3"),
    ["image24.raw"] = (98304, "cd655449b73c7c060329d57e3073cfe72ab8e301c55cc421e464ff3cf8da1be4"),
    ["pattern.bin"] = (192000, "c0494f56095032935d2181dbca9d9e8beaeac26a91a6fab87ef79088b79825d8"),
    ["empty.dat"] = (0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
    ["mixed.bin"] = (35003, "beb2920ee28f81b77e0a3c5feca91437770c6ebb537c6d73b6b11c5aa4500905"),
    ["zeros.bin"] = (200000, "4cbbd9be0cba685835755f827758705db5a413c5494c34262cd25946a73e7582"),
    ["biased.bin"] = (120000, "a2e40523e3bcada46341099904439de3f92970c827d5f9928f216942ad7654c4"),
    ["records.bin"] = (240000, "632c93fba25d5906d55b1dfb13195e45ecf8e4b5555a912600d65fdb81cb28b7"),
    ["Noise.wav"] = (135202, "0d897df3862192ea078efc1dd8fdc4f51fae9e93d3ed4c15e049829b0386729e"),
    ["test.wav"] = (17410, "5d169a17a6bb134a3ec042a9c131cdee6100aa3ae278b98cd5fb89840dac96dd"),
    ["e_sin8.wav"] = (45, "b252c340524340648bd30bb1a54705afb15f8158ed92fe869fee596e72a2d164"),
    ["e_st8.wav"] = (46, "f1f6d6c358bb9f755b38aff600416a4b200fd3338a888b813a4063ae5590ea80"),
    ["x_alt8.wav"] = (5047, "057ffb7aa1262581d0e5ca9e59c2c05033177d978828b86d078fbecfb592cbf7"),
    ["sine_st8.wav"] = (24044, "8ad1101ac0c6d473444f8f12afacec6a9fba9f0e63f2158c50b6bddb12f72fa1"),
    ["x_altst16.wav"] = (36908, "f1bec2c4f0f0afaa97555631bf9e7c8e74a7410b6be7127ab4104054c16d0f28"),
    ["x_5003st16.wav"] = (20056, "a97294f56b7eb4c96d1d4705ffb567f460caceec5ada41dcab15bae171479449"),
    ["x_ramp16.wav"] = (18480, "ac4755134fc6f24d871babfeee80378034bc5a4a0e27b91eff3e887f93260d04"),
  };

  private static IEnumerable<string> Fixtures() => Assembly.GetExecutingAssembly().GetManifestResourceNames()
    .Where(n => n.StartsWith("SqxVectors.", StringComparison.Ordinal) && !n.StartsWith("SqxVectors.refused.", StringComparison.Ordinal) && !n.StartsWith("SqxVectors.pw.", StringComparison.Ordinal) && n.EndsWith(".sqx", StringComparison.Ordinal))
    .Select(n => n["SqxVectors.".Length..]).Order();

  [TestCaseSource(nameof(Fixtures)), Category("HappyPath")]
  public void GivenAReferenceArchive_WhenExtracted_ThenEveryFileIsTheOriginal(string fixture) {
    using var reader = new SqxReader(new MemoryStream(Resource(fixture)));
    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Is.Not.Empty);
    foreach (var entry in files) {
      var data = reader.ExtractEntry(entry);
      var name = Path.GetFileName(entry.FileName);
      Assert.That(Originals.ContainsKey(name), Is.True, $"{fixture}: unexpected member {entry.FileName}");
      Assert.Multiple(() => {
        Assert.That(data.LongLength, Is.EqualTo(Originals[name].Size), $"{fixture}: {name} size");
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(Originals[name].Sha256), $"{fixture}: {name} content");
      });
    }
  }

  [Test, Category("HappyPath")]
  public void GivenTheSampleText_WhenWrittenAndReadBack_ThenItRoundTripsInBothFormats() {
    foreach (var format in new[] { SqxTables.Format11, SqxTables.Format20 })
      for (byte method = 1; method <= 4; ++method) {
        var writer = new SqxWriter(method, 64 * 1024, format);
        writer.AddFile("dir/text.txt", CodecProofData.Text(24_000));
        writer.AddFile("mixed.bin", CodecProofData.Mixed(6_000));
        writer.AddFile("empty", []);
        using var reader = new SqxReader(new MemoryStream(writer.ToArray()));
        Assert.That(reader.Entries.Select(e => e.FileName), Is.EqualTo(new[] { "dir/text.txt", "mixed.bin", "empty" }));
        Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(CodecProofData.Text(24_000)), $"format {format} method {method}");
        Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(CodecProofData.Mixed(6_000)), $"format {format} method {method}");
        Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.Empty);
      }
  }

  [Test, Category("Exception")]
  public void GivenACorruptHeader_WhenOpened_ThenTheBlockChecksumRefusesIt() {
    var archive = Resource(Fixtures().First());
    archive[30] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => _ = new SqxReader(new MemoryStream(archive)));
  }

  [Test, Category("Exception")]
  public void GivenATextArchiveWithACorruptPayload_WhenExtracted_ThenItIsRefused() {
    var archive = Resource("eng_v2.0_ultra.sqx");
    archive[archive.Length / 2] ^= 0x20;
    using var reader = new SqxReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(6));
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [Test, Category("Exception")]
  public void GivenATextStreamThatContinuesAMissingModel_WhenDecoded_ThenItIsRefused() {
    // the first bit set: "continue the model of the file before", with no file before
    var stream = new byte[16];
    stream[3] = 0x80;
    Assert.Throws<InvalidDataException>(() => new SqxPpmdDecoder().Decode(stream, 10));
  }

  [Test, Category("Exception")]
  public void GivenAnUltraArchiveWithACorruptPayload_WhenExtracted_ThenItIsRefused() {
    var archive = Resource("mixed_v2.0_ultra.sqx");
    archive[archive.Length / 2] ^= 0x20;
    using var reader = new SqxReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(7));
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [TestCase("Noise_v1.1_none.sqx"), TestCase("Noise_v2.0_maximum.sqx"), TestCase("sine_st8_v2.0_audio.sqx"), Category("Exception")]
  public void GivenAnAudioArchiveWithACorruptPayload_WhenExtracted_ThenItIsRefused(string fixture) {
    var archive = Resource(fixture);
    archive[archive.Length / 2] ^= 0x20;
    using var reader = new SqxReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(5));
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [Test, Category("Exception")]
  public void GivenAnAudioHeaderThatDoesNotDescribeTheFile_WhenDecoded_ThenItIsRefused() {
    var stream = new byte[64];   // skip 0, tail 0, raw 0: describes an empty file, not one of 10 bytes
    stream[15] = 1;               // one channel (header byte 12, the fourth byte of the fourth word)
    stream[14] = 16;
    Assert.Throws<InvalidDataException>(() => new SqxAudioDecoder().Decode(stream, 10, 20));
  }

  [Test, Category("Exception")]
  public void GivenSomethingElse_WhenOpened_ThenItIsNotAnSqxArchive()
    => Assert.Throws<InvalidDataException>(() => _ = new SqxReader(new MemoryStream(new byte[100])));

  [Test, Category("Exception")]
  public void GivenACorruptPayload_WhenExtracted_ThenTheFileCrcRefusesIt() {
    var writer = new SqxWriter();
    writer.AddFile("text.txt", CodecProofData.Text(4_000));
    var archive = writer.ToArray();
    archive[^20] ^= 0x10;
    using var reader = new SqxReader(new MemoryStream(archive));
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [Test, Category("ArchiveExternal"), Category("HappyPath")]
  public void SqueezCExtractsOurArchives() {
    foreach (var format in new[] { SqxTables.Format11, SqxTables.Format20 })
      for (byte method = 1; method <= 4; ++method) {
        var writer = new SqxWriter(method, 256 * 1024, format);
        var files = new Dictionary<string, byte[]> {
          ["text.txt"] = CodecProofData.Text(60_000),
          ["mixed.bin"] = CodecProofData.Mixed(20_000),
          ["zeros.bin"] = new byte[70_000],
          ["one.bin"] = [0x42],
        };
        foreach (var (name, data) in files)
          writer.AddFile(name, data);
        ToolExtracts(writer.ToArray(), files);
      }
  }

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SqxVectors." + name)
      ?? throw new InvalidOperationException("Missing resource SqxVectors." + name);
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static void ToolExtracts(byte[] archive, Dictionary<string, byte[]> expected) {
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_SQX");
    if (string.IsNullOrEmpty(tool))
      tool = "sqx";
    var dir = Path.Combine(Path.GetTempPath(), "cwb-sqx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "ours.sqx");
      File.WriteAllBytes(path, archive);
      var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
      var outDir = Path.Combine(dir, "out");
      foreach (var argument in new[] { "x", outDir, path })
        start.ArgumentList.Add(argument);
      Process process;
      try {
        process = Process.Start(start)!;
      } catch (Win32Exception) {
        Assert.Ignore("Squeez-c's sqx is not on the PATH (or set CWB_TOOL_SQX).");
        return;
      }

      string output;
      using (process) {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        output = stdout.Result + stderr;
        Assert.That(process.ExitCode, Is.EqualTo(0), $"sqx refused our archive:\n{output}");
      }

      foreach (var (name, data) in expected)
        Assert.That(File.ReadAllBytes(Path.Combine(dir, "out", name)), Is.EqualTo(data), $"{name}\n{output}");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
