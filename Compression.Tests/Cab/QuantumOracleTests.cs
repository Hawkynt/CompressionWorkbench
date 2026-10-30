using System.Reflection;
using Compression.Core.Dictionary.Quantum;
using Compression.Tests.CodecProof;
using FileFormat.Cab;

namespace Compression.Tests.Cab;

/// <summary>
/// Quantum against third parties: a cabinet Microsoft's tools wrote decodes, our encoder reproduces
/// its Quantum block byte for byte, and 7-Zip (which, unlike cabextract, insists that a block ends
/// exactly where its last bits do) extracts our multi-block folders.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class QuantumOracleTests {
  private static readonly byte[] QtmText = "If you can read this, the Quantum decompressor is working!\n"u8.ToArray();

  // The single CFDATA block of mixed.cab's Quantum:18 folder, as Microsoft wrote it.
  private static readonly byte[] MicrosoftBlock = Convert.FromHexString(
    "d606690bcb47f02c2a3a8f2cabbb3cb933018bd8584b7b01ba6f6d516e3ac367424beb023643d66656ca9e72cc300000");

  [Test]
  public void Given_MicrosoftsQuantumFolder_When_Extracted_Then_TheTextComesBack() {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CabQuantumVectors.libmspack-mixed.cab")!;
    using var reader = new CabReader(stream);
    Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.FileName == "qtm.txt")), Is.EqualTo(QtmText));
  }

  [Test]
  public void Given_TheSameText_When_WeCompressIt_Then_OurBlockIsMicrosoftsByteForByte() {
    var folder = QuantumCompressor.CompressFolder(QtmText, 0, 18);
    Assert.That(folder.Consumed, Is.EqualTo(QtmText.Length));
    Assert.That(folder.Compressed, Is.EqualTo(MicrosoftBlock));
  }

  [TestCase(15, "text")]
  [TestCase(21, "text")]
  [TestCase(15, "zeros")]
  [TestCase(21, "mixed")]
  [Category("ExternalInterop")]
  public void Given_OurMultiBlockQuantumFolder_When_7ZipExtracts_Then_TheBytesMatch(int windowBits, string kind) {
    FsInteropToolbox.Require7z();
    var data = kind switch {
      "text" => CodecProofData.Text(150_000),
      "zeros" => new byte[100_000],
      _ => CodecProofData.Mixed(120_000),
    };
    var dir = Path.Combine(Path.GetTempPath(), "cwb-quantum-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var cab = Path.Combine(dir, "ours.cab");
      var writer = new CabWriter(CabCompressionType.Quantum, quantumWindowBits: windowBits);
      writer.AddFile("data.bin", data);
      using (var file = File.Create(cab))
        writer.WriteTo(file);

      var outDir = Path.Combine(dir, "out");
      var (stdout, stderr, code) = FsInteropToolbox.Run7z($"x -y \"-o{outDir}\" \"{cab}\"");
      Assert.That(code, Is.Zero, stdout + stderr);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "data.bin")), Is.EqualTo(data));
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
