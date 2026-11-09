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
  };

  private static IEnumerable<string> Fixtures() => Assembly.GetExecutingAssembly().GetManifestResourceNames()
    .Where(n => n.StartsWith("SqxVectors.", StringComparison.Ordinal) && !n.StartsWith("SqxVectors.refused.", StringComparison.Ordinal) && n.EndsWith(".sqx", StringComparison.Ordinal))
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
  public void GivenAReferenceArchiveInMethod6_WhenExtracted_ThenTheTextMethodIsRefused() {
    // hello_v2.0_normal.sqx stores hello.txt with method 6 (PPMd), which this reader does not decode.
    using var reader = new SqxReader(new MemoryStream(Resource("refused.hello_v2.0_normal.sqx")));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(6));
    Assert.Throws<NotSupportedException>(() => reader.ExtractEntry(reader.Entries[0]));
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
