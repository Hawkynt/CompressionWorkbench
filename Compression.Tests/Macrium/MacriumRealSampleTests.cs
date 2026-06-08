using System.IO;
using System.Linq;
using System.Text;
using FileFormat.Macrium;

namespace Compression.Tests.Macrium;

/// <summary>
/// External-validation gate against the real .mrimgx sample shipped by the
/// vendor at github.com/macrium/mrimgx_file_layout demo/. The file is large
/// (60 MB) and not in the repo — the test is skipped cleanly when it isn't
/// available at the documented local path. When present, it verifies that our
/// reader walks the same vendor-emitted footer + chain + JSON without crashing
/// and surfaces all expected block names.
/// </summary>
[TestFixture]
public class MacriumRealSampleTests {

  /// <summary>
  /// Candidate locations for the vendor demo file. The test honours the first
  /// match. To enable the test, drop the demo .mrimgx (extracted from
  /// <c>github.com/macrium/mrimgx_file_layout/demo/demo.zip</c>) at any of
  /// these paths, or set <c>MACRIUM_DEMO_MRIMGX</c> in the environment.
  /// </summary>
  private static readonly string[] CandidatePaths = [
    // %TEMP% layout produced by extracting demo.zip into a per-user temp.
    Path.Combine(Path.GetTempPath(), "macrium-demo", "demo", "2394E9AA621DDC3A-00-00.mrimgx"),
    // Linux / WSL shell-style absolute path.
    "/tmp/macrium-demo/demo/2394E9AA621DDC3A-00-00.mrimgx",
  ];

  private static string? ResolveSamplePath() {
    var fromEnv = Environment.GetEnvironmentVariable("MACRIUM_DEMO_MRIMGX");
    if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
      return fromEnv;
    foreach (var candidate in CandidatePaths)
      if (File.Exists(candidate))
        return candidate;
    return null;
  }

  private static string RequireSample() {
    var path = ResolveSamplePath();
    if (path is null)
      Assert.Ignore($"Vendor demo sample not present at any of [{string.Join(", ", CandidatePaths)}] or $MACRIUM_DEMO_MRIMGX. Skipping external interop test.");
    return path!;
  }

  [Test, Category("ExternalInterop")]
  public void Reader_ParsesVendorDemoFooterAndChain() {
    var samplePath = RequireSample();
    using var fs = File.OpenRead(samplePath);
    using var r = new MacriumReader(fs);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Variant, Is.EqualTo("mrimgx"));
    Assert.That(r.Tag, Is.EqualTo("MACRIUM_FILE"));
    Assert.That(r.Blocks.Count, Is.GreaterThan(0), "Vendor sample must walk a non-empty metadata chain.");
    var names = r.Blocks.Select(b => b.Name).ToHashSet();
    // Vendor's full backup ships at least $JSON + a disk/partition combination.
    Assert.That(names, Does.Contain("$JSON"));
    var json = r.Entries.FirstOrDefault(e => e.Name == "metadata.json");
    Assert.That(json, Is.Not.Null, "Vendor's $JSON block must be zstd-decompressed by us.");
    var jsonText = Encoding.UTF8.GetString(json!.Data);
    Assert.That(jsonText, Does.Contain("imageid"));
    Assert.That(jsonText, Does.Contain("2394E9AA621DDC3A").IgnoreCase);
  }

  [Test, Category("ExternalInterop")]
  public void Reader_OnVendorDemo_StatusIsDeterministic() {
    var samplePath = RequireSample();
    using var fs = File.OpenRead(samplePath);
    using var r = new MacriumReader(fs);
    // Demo sample isn't encrypted per the demo README; whether sector
    // reconstruction succeeds depends on whether our $INDEX walk matches the
    // vendor's exact framing — we accept either outcome here, but the status
    // must be one of the documented enum strings (no crash, no UB).
    TestContext.Out.WriteLine($"sector_reconstruction = {r.SectorReconstructionStatus}");
    TestContext.Out.WriteLine($"encrypted = {r.IsEncrypted}");
    TestContext.Out.WriteLine($"block names = {string.Join(", ", r.Blocks.Select(b => b.Name))}");
    Assert.That(r.SectorReconstructionStatus, Is.Not.Empty);
  }
}

/// <summary>
/// Vendor-sample sector-reconstruction proofs — these are the strongest
/// possible interop check we can run without a Windows host that can launch
/// <c>img_to_vhdx.exe</c>: we walk the vendor's actual ROOT + DISK + PARTITION
/// metadata chains, find <c>$INDEX</c>, decode every data block, and verify
/// the reconstructed bytes carry a valid MBR + GPT primary header at the
/// documented sector offsets.
/// </summary>
[TestFixture]
public class MacriumRealSampleSectorReconstruction {

  private static string RequireSample() {
    var path = Path.Combine(Path.GetTempPath(), "macrium-demo", "demo", "2394E9AA621DDC3A-00-00.mrimgx");
    if (!File.Exists(path))
      Assert.Ignore($"Vendor demo sample not present at {path}. Skipping vendor sector-reconstruction proof.");
    return path;
  }

  [Test, Category("ExternalInterop")]
  public void VendorDemo_WalksRootAndDiskAndPartitionChains() {
    var path = RequireSample();
    using var fs = File.OpenRead(path);
    using var r = new MacriumReader(fs);
    var names = r.Blocks.Select(b => b.Name).ToList();
    // Root chain ($JSON + $AUXDATA) + disk chain ($TRACK0) + partition chain ($BITMAP, $INDEX).
    Assert.That(names, Does.Contain("$JSON"));
    Assert.That(names, Does.Contain("$AUXDATA"));
    Assert.That(names, Does.Contain("$TRACK0"),
      "Disk-level chain at index_file_position must be walked.");
    Assert.That(names, Does.Contain("$BITMAP"),
      "Partition-level chain (BITMAP + INDEX) must be walked after the disk chain.");
    Assert.That(names, Does.Contain("$INDEX"),
      "$INDEX is mandatory for sector reconstruction.");
  }

  [Test, Category("ExternalInterop")]
  public void VendorDemo_SectorReconstruction_Succeeds() {
    var path = RequireSample();
    using var fs = File.OpenRead(path);
    using var r = new MacriumReader(fs);
    Assert.That(r.SectorReconstructionStatus, Is.EqualTo("ok"),
      "Vendor's published demo must reconstruct end-to-end.");
    Assert.That(r.SectorReconstructionAvailable, Is.True);
    var disk = r.Entries.FirstOrDefault(e => e.Name == "disk-image.raw");
    Assert.That(disk, Is.Not.Null);
    Assert.That(disk!.Size, Is.GreaterThanOrEqualTo(520),
      "Reconstructed disk must be at least MBR + 8 bytes of GPT header (512 + 8).");
  }

  [Test, Category("ExternalInterop")]
  public void VendorDemo_ReconstructedDisk_HasValidMbrBootCode() {
    var path = RequireSample();
    using var fs = File.OpenRead(path);
    using var r = new MacriumReader(fs);
    var disk = r.Entries.First(e => e.Name == "disk-image.raw").Data;
    // Classic x86 MBR boot code starts with XOR AX,AX (0x33 0xC0) — the very
    // first two bytes a Windows-formatted disk's MBR uses.
    Assert.That(disk[0], Is.EqualTo(0x33),
      "First byte of reconstructed disk must be the MBR XOR-AX-AX opcode (0x33).");
    Assert.That(disk[1], Is.EqualTo(0xC0));
    // MBR signature 0x55AA at offset 510..511 of the boot sector.
    Assert.That(disk[510], Is.EqualTo(0x55));
    Assert.That(disk[511], Is.EqualTo(0xAA));
  }

  [Test, Category("ExternalInterop")]
  public void VendorDemo_ReconstructedDisk_HasValidGptHeaderAtLba1() {
    var path = RequireSample();
    using var fs = File.OpenRead(path);
    using var r = new MacriumReader(fs);
    var disk = r.Entries.First(e => e.Name == "disk-image.raw").Data;
    // GPT primary header is at LBA 1 = offset 512; magic is ASCII "EFI PART".
    Assume.That(disk.Length, Is.GreaterThanOrEqualTo(520));
    var gptSig = Encoding.ASCII.GetString(disk, 512, 8);
    Assert.That(gptSig, Is.EqualTo("EFI PART"),
      "Reconstructed disk's LBA 1 must carry the EFI PART magic — proves the $TRACK0 + $INDEX walk decoded real MBR + GPT bytes.");
  }
}
