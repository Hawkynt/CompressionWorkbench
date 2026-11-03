using System.Diagnostics;
using FileFormat.Cab;
using FileFormat.Zip;

namespace Compression.Tests.CodecProof;

/// <summary>
/// Container codecs against third-party writers and readers: 7-Zip for the ZIP methods and WIM,
/// Windows' makecab and expand for cabinet MSZIP and LZX. The 7z coders, ZIP Shrink, Implode,
/// LZMA and PPMd, cabinet Quantum and CHM have proofs of their own (see
/// <c>docs/CODEC-PROOF-AUDIT.md</c>).
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
[Category("ExternalInterop")]
public class ArchiveCodecOracleTests {
  private string _dir = "";

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-codec-proof-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  private static (string Name, byte[] Data)[] Payload() => [
    ("text.txt", CodecProofData.Text(300_000)),
    ("mixed.bin", CodecProofData.Mixed(120_000)),
    ("random.bin", CodecProofData.Random(40_000)),
    ("one.bin", [0x5A]),
    ("empty.bin", []),
  ];

  private (string Name, byte[] Data)[] WritePayload() {
    var payload = Payload();
    foreach (var (name, data) in payload)
      File.WriteAllBytes(Path.Combine(this._dir, name), data);
    return payload;
  }

  private string Inputs(IEnumerable<string> names) => string.Join(' ', names.Select(n => $"\"{Path.Combine(this._dir, n)}\""));

  // ---------- ZIP methods ----------

  [TestCase("Deflate")]
  [TestCase("Deflate64")]
  [TestCase("BZip2")]
  [TestCase("LZMA")]
  [TestCase("Copy")]
  public void Zip_7ZipWrites_WeRead(string method) {
    FsInteropToolbox.Require7z();
    var payload = this.WritePayload();
    var archive = Path.Combine(this._dir, "a.zip");
    var (o, e, code) = FsInteropToolbox.Run7z($"a -tzip -mm={method} \"{archive}\" {this.Inputs(payload.Select(p => p.Name))}");
    Assert.That(code, Is.Zero, o + e);

    using var reader = new ZipReader(File.OpenRead(archive));
    foreach (var (name, data) in payload)
      Assert.That(reader.ExtractEntry(reader.Entries.Single(x => x.FileName == name)), Is.EqualTo(data), name);
  }

  [TestCase(ZipCompressionMethod.Deflate)]
  [TestCase(ZipCompressionMethod.Deflate64)]
  [TestCase(ZipCompressionMethod.BZip2)]
  [TestCase(ZipCompressionMethod.Xz)]
  [TestCase(ZipCompressionMethod.Zstd)]
  public void Zip_WeWrite_7ZipReads(ZipCompressionMethod method) {
    FsInteropToolbox.Require7z();
    var payload = Payload();
    var archive = Path.Combine(this._dir, "ours.zip");
    using (var file = File.Create(archive))
    using (var writer = new ZipWriter(file)) {
      foreach (var (name, data) in payload)
        writer.AddEntry(name, data, method);
      writer.Finish();
    }

    this.Assert7ZipExtracts(archive, payload);
  }

  // ---------- cabinets ----------

  [TestCase("MSZIP", "")]
  [TestCase("LZX", "15")]
  [TestCase("LZX", "18")]
  [TestCase("LZX", "21")]
  public void Cab_MakecabWrites_WeRead(string type, string memory) {
    var makecab = Path.Combine(Environment.SystemDirectory, "makecab.exe");
    if (!OperatingSystem.IsWindows() || !File.Exists(makecab))
      Assert.Ignore("makecab.exe is the oracle here.");

    foreach (var (name, data) in this.WritePayload().Where(p => p.Data.Length > 0)) {
      var cab = Path.Combine(this._dir, name + ".cab");
      var mem = memory.Length > 0 ? $"/D CompressionMemory={memory}" : "";
      var (o, e, code) = Run(makecab, $"/D CompressionType={type} {mem} \"{Path.Combine(this._dir, name)}\" \"{cab}\"");
      Assert.That(code, Is.Zero, o + e);

      using var reader = new CabReader(File.OpenRead(cab));
      Assert.That(reader.ExtractEntry(reader.Entries.Single()), Is.EqualTo(data), $"{type}:{memory} {name}");
    }
  }

  [TestCase(CabCompressionType.MsZip, 15)]
  [TestCase(CabCompressionType.Lzx, 15)]
  [TestCase(CabCompressionType.Lzx, 21)]
  public void Cab_WeWrite_ExpandAnd7ZipRead(CabCompressionType type, int windowBits) {
    var payload = Payload();
    var cab = Path.Combine(this._dir, "ours.cab");
    var writer = new CabWriter(type, lzxWindowBits: windowBits);
    foreach (var (name, data) in payload)
      writer.AddFile(name, data);
    using (var file = File.Create(cab))
      writer.WriteTo(file);

    var expand = Path.Combine(Environment.SystemDirectory, "expand.exe");
    if (OperatingSystem.IsWindows() && File.Exists(expand)) {
      var outDir = Path.Combine(this._dir, "expand-out");
      Directory.CreateDirectory(outDir);
      var (o, e, code) = Run(expand, $"\"{cab}\" -F:* \"{outDir}\"");
      Assert.That(code, Is.Zero, o + e);
      foreach (var (name, data) in payload)
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, name)), Is.EqualTo(data), "expand " + name);
    }

    if (FsInteropToolbox.SevenZipAvailable)
      this.Assert7ZipExtracts(cab, payload);
  }

  // ---------- WIM ----------

  [TestCase(FileFormat.Wim.WimConstants.CompressionXpress)]
  [TestCase(FileFormat.Wim.WimConstants.CompressionLzx)]
  [TestCase(FileFormat.Wim.WimConstants.CompressionLzms)]
  [TestCase(FileFormat.Wim.WimConstants.CompressionNone)]
  public void Wim_WeWrite_7ZipReads(uint compression) {
    var payload = Payload();
    var wim = Path.Combine(this._dir, "ours.wim");
    using (var file = File.Create(wim))
      new FileFormat.Wim.WimWriter(file, compression).Write(payload.Select(p => (p.Name, p.Data)).ToList());

    this.Assert7ZipExtracts(wim, payload.Where(p => p.Data.Length > 0).ToArray());
  }

  private void Assert7ZipExtracts(string archive, (string Name, byte[] Data)[] payload) {
    FsInteropToolbox.Require7z();
    var outDir = Path.Combine(this._dir, "7z-out-" + Path.GetFileName(archive));
    var (o, e, code) = FsInteropToolbox.Run7z($"x -y \"-o{outDir}\" \"{archive}\"");
    Assert.That(code, Is.Zero, o + e);
    foreach (var (name, data) in payload)
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, name)), Is.EqualTo(data), "7z " + name);
  }

  private static (string StdOut, string StdErr, int ExitCode) Run(string tool, string args) {
    var psi = new ProcessStartInfo(tool, args) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    using var process = Process.Start(psi)!;
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit(120_000);
    return (stdout, stderr, process.ExitCode);
  }
}
