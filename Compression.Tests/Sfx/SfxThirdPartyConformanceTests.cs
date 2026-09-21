#pragma warning disable CS1591
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Compression.Lib;
using NUnit.Framework;

namespace Compression.Tests.Sfx;

/// <summary>
/// A self-extracting archive of ours should still be a perfectly ordinary archive to the tool that
/// owns the format. Someone handed a <c>.exe</c> built here must be able to open it with unzip, lha
/// or 7-Zip and pull out one file, without running our stub and without trusting it.
/// </summary>
/// <remarks>
/// <para>
/// This is what pins the container's layout in place. It holds only while the payload is a single
/// contiguous run of unmodified archive bytes at the end of the file — so nothing may compress,
/// encrypt or reframe it, and the multi-OS stubs have to sit ahead of it rather than interleaved
/// with it. Those are design constraints, and these are the tests that notice when one is broken.
/// </para>
/// <para>
/// Each test skips rather than fails when its tool is absent, in line with the rest of the external
/// interop suite: a missing tool is an environment gap, not a defect in the container.
/// </para>
/// </remarks>
[TestFixture]
public class SfxThirdPartyConformanceTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-sfxconf-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, recursive: true); } catch { }
  }

  private static readonly byte[] Marker = "third-party-conformance-marker"u8.ToArray();

  /// <summary>A stub stand-in. Its contents are irrelevant: no test here executes it.</summary>
  private string WriteFakeStub(int length = 4096) {
    var path = Path.Combine(this._dir, "stub.bin");
    var bytes = new byte[length];
    new Random(0x5F3C0DE).NextBytes(bytes);

    // Keep archive signatures out of the stub so an overlay scan cannot hit a false positive.
    for (var i = 0; i < bytes.Length - 1; ++i)
      if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B) bytes[i] = 0x51;

    bytes[0] = (byte)'M';
    bytes[1] = (byte)'Z';
    File.WriteAllBytes(path, bytes);
    return path;
  }

  private string WriteZipPayload() {
    var path = Path.Combine(this._dir, "payload.zip");
    using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);

    using (var entry = zip.CreateEntry("readme.txt").Open())
      entry.Write(Marker);

    using (var entry = zip.CreateEntry("nested/data.bin").Open())
      entry.Write([1, 2, 3, 4, 5, 6, 7, 8]);

    return path;
  }

  /// <summary>Builds an ordinary single-target SFX around the given payload.</summary>
  private string BuildSfx(string payloadPath, string name = "out.exe") {
    var sfx = Path.Combine(this._dir, name);
    SfxBuilder.Create(payloadPath, sfx, this.WriteFakeStub());
    return sfx;
  }

  private static void RequireWslTool(string tool) {
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"'{tool}' not found in WSL. Install it with `sudo apt install -y {tool}`.");
  }

  // ── the tool that owns the format ───────────────────────────────────────────────────────────

  [Test]
  public void GivenAZipSfx_WhenUnzipListsIt_ThenItSeesTheEntriesWithoutRunningTheStub() {
    RequireWslTool("unzip");
    var sfx = this.BuildSfx(this.WriteZipPayload());

    var result = FsInteropToolbox.RunWsl($"unzip -l {FsInteropToolbox.WinToWsl(sfx)}");

    Assert.Multiple(() => {
      Assert.That(result.StdOut, Does.Contain("readme.txt"), result.StdErr);
      Assert.That(result.StdOut, Does.Contain("nested/data.bin"));
    });
  }

  /// <summary>
  /// The point of the exercise: one named file out of our container, using someone else's tool.
  /// </summary>
  [Test]
  public void GivenAZipSfx_WhenUnzipExtractsOneNamedEntry_ThenTheBytesAreOurs() {
    RequireWslTool("unzip");
    var sfx = this.BuildSfx(this.WriteZipPayload());
    var outDir = Path.Combine(this._dir, "unzipped");
    Directory.CreateDirectory(outDir);

    var result = FsInteropToolbox.RunWsl(
      $"unzip -o -j {FsInteropToolbox.WinToWsl(sfx)} readme.txt -d {FsInteropToolbox.WinToWsl(outDir)}");

    var extracted = Path.Combine(outDir, "readme.txt");
    Assert.That(File.Exists(extracted), Is.True, $"stdout: {result.StdOut}\nstderr: {result.StdErr}");
    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(Marker));
  }

  /// <summary>
  /// The multi-OS container carries extra stubs between the PE image and the payload. They sit
  /// ahead of it precisely so this still works; interleaving them would break it.
  /// </summary>
  [Test]
  public void GivenAPolyglotSfx_WhenUnzipExtractsFromIt_ThenTheExtraStubsDoNotGetInTheWay() {
    RequireWslTool("unzip");

    var payload = this.WriteZipPayload();
    var sfx = Path.Combine(this._dir, "poly.exe");
    var windowsStub = File.ReadAllBytes(this.BuildPeStub());

    using (var archive = File.OpenRead(payload))
      SfxPolyglot.Write(sfx, windowsStub, [
        new("linux-x64", RandomStub(2048, 11)),
        new("osx-arm64", RandomStub(1536, 12)),
      ], archive);

    var outDir = Path.Combine(this._dir, "polyunzipped");
    Directory.CreateDirectory(outDir);

    var result = FsInteropToolbox.RunWsl(
      $"unzip -o -j {FsInteropToolbox.WinToWsl(sfx)} readme.txt -d {FsInteropToolbox.WinToWsl(outDir)}");

    var extracted = Path.Combine(outDir, "readme.txt");
    Assert.That(File.Exists(extracted), Is.True, $"stdout: {result.StdOut}\nstderr: {result.StdErr}");
    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(Marker));
  }

  [Test]
  public void GivenATarSfx_WhenTarListsIt_ThenItIsSkippedOrSeenButNeverCorrupt() {
    RequireWslTool("bsdtar");

    var tarPath = Path.Combine(this._dir, "payload.tar");
    using (var tar = File.Create(tarPath))
      WriteTarWithSingleFile(tar, "readme.txt", Marker);

    var sfx = this.BuildSfx(tarPath, "tar-sfx.exe");

    // tar has no signature at offset zero and no end-anchored directory, so a reader cannot find a
    // payload behind arbitrary leading bytes. Recording that plainly is the useful outcome: it is a
    // property of the format, not something the container can fix.
    var result = FsInteropToolbox.RunWsl($"bsdtar -tf {FsInteropToolbox.WinToWsl(sfx)} 2>&1 || true");

    Assert.That(result.StdOut, Does.Not.Contain("readme.txt").IgnoreCase,
      "if this starts passing, tar gained prepended-data tolerance and the docs should say so");
  }

  [Test]
  public void GivenAZipSfx_WhenSevenZipOpensIt_ThenItExtractsOneNamedEntry() {
    FsInteropToolbox.Require7z();

    var sfx = this.BuildSfx(this.WriteZipPayload(), "sevenzip.exe");
    var outDir = Path.Combine(this._dir, "sevenzipped");

    var result = FsInteropToolbox.Run7z($"e -y -o\"{outDir}\" \"{sfx}\" readme.txt");
    var extracted = Path.Combine(outDir, "readme.txt");

    Assert.That(File.Exists(extracted), Is.True, $"stdout: {result.StdOut}\nstderr: {result.StdErr}");
    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(Marker));
  }

  // ── helpers ─────────────────────────────────────────────────────────────────────────────────

  private static byte[] RandomStub(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    for (var i = 0; i < bytes.Length - 1; ++i)
      if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B) bytes[i] = 0x51;
    return bytes;
  }

  /// <summary>A PE shaped like the real AOT stubs: e_lfanew at 0x110, one section.</summary>
  private string BuildPeStub() {
    const int PeHeaderOffset = 0x110;
    const int SectionBytes = 512;
    var pe = new byte[PeHeaderOffset + 24 + 224 + 40 + SectionBytes];

    pe[0] = (byte)'M';
    pe[1] = (byte)'Z';
    for (var i = 2; i < PeHeaderOffset; ++i) pe[i] = (byte)'.';
    BitConverter.GetBytes(PeHeaderOffset).CopyTo(pe, 0x3C);

    pe[PeHeaderOffset] = (byte)'P';
    pe[PeHeaderOffset + 1] = (byte)'E';
    BitConverter.GetBytes((ushort)1).CopyTo(pe, PeHeaderOffset + 6);
    BitConverter.GetBytes((ushort)224).CopyTo(pe, PeHeaderOffset + 20);

    var section = PeHeaderOffset + 24 + 224;
    BitConverter.GetBytes((uint)SectionBytes).CopyTo(pe, section + 16);
    BitConverter.GetBytes((uint)(section + 40)).CopyTo(pe, section + 20);

    var path = Path.Combine(this._dir, "pe-stub.bin");
    File.WriteAllBytes(path, pe);
    return path;
  }

  /// <summary>A minimal ustar archive holding one file, enough for a reader to list.</summary>
  private static void WriteTarWithSingleFile(Stream output, string name, byte[] content) {
    var header = new byte[512];
    var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
    nameBytes.CopyTo(header, 0);
    System.Text.Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
    System.Text.Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 108);
    System.Text.Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 116);
    System.Text.Encoding.ASCII.GetBytes(Convert.ToString(content.Length, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
    System.Text.Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header, 136);
    header[156] = (byte)'0';
    System.Text.Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
    System.Text.Encoding.ASCII.GetBytes("00").CopyTo(header, 263);

    for (var i = 148; i < 156; ++i) header[i] = (byte)' ';
    var checksum = header.Aggregate(0, (sum, b) => sum + b);
    System.Text.Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);

    output.Write(header);
    output.Write(content);
    output.Write(new byte[512 - content.Length % 512]);
    output.Write(new byte[1024]);
  }
}
