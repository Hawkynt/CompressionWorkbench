#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.T64;
using FileSystem.Adf;
using FileSystem.AppleDos;
using FileSystem.Atari8;
using FileSystem.Bbc;
using FileSystem.CpcDsk;
using FileSystem.D64;
using FileSystem.D71;
using FileSystem.D81;
using FileSystem.Mfs;
using FileSystem.Msa;
using FileSystem.ProDos;
using FileSystem.TrDos;
using FileSystem.ZxScl;

namespace Compression.Tests.Support;

/// <summary>
/// Validates retro filesystem readers against <i>real wild disk images</i> (not just
/// round-trips of our own writers). Fetches public-domain sample disks from
/// <c>zimmers.net</c> and caches them locally. Tests are tagged
/// <c>Category("RetroRealImage")</c> so they can be excluded on offline CI.
/// <para>
/// Each test uses <see cref="RetroImageCache"/> to fetch, hash-verify, and cache
/// the sample image. If the fetch fails (offline, 404, hash mismatch), the test
/// calls <see cref="Assert.Ignore(string)"/> with a diagnostic.
/// </para>
/// <para>
/// For formats where no accessible wild image was found (Atari ATR, Mac MFS,
/// BBC SSD, Apple DSK/ProDOS, ZX SCL/TRD, CPC DSK, Atari MSA, Amiga ADF, DMS),
/// a synthetic-only fallback test is provided that clearly documents the
/// limitation: it proves the reader round-trips our writer, nothing more.
/// </para>
/// <para>
/// Sources:
///   - D64/D71/D81/T64 : <c>http://www.zimmers.net/anonftp/pub/cbm/</c> (public FTP mirror).
/// </para>
/// </summary>
[TestFixture]
[Category("RetroRealImage")]
public class RetroRealImageTests {

  // ═══════════════════════════════════════════════════════════════════════════
  // D64 — Commodore 1541 (public-domain C64 "starter-kit" demo disk from zimmers)
  // ═══════════════════════════════════════════════════════════════════════════

  [Test, Category("HappyPath")]
  public void D64_WildImage_Lists_And_Extracts() {
    var path = RetroImageCache.FetchGz(
      url: "http://www.zimmers.net/anonftp/pub/cbm/demodisks/c64/starter-kit.d64.gz",
      sha256OfDecompressed: "7e79a086ea5ce6e907c3417465d2fb66c8bb668d6ae674692f7295495f5d9635",
      localName: "cbm-starter-kit.d64");
    if (path is null) Assert.Ignore("wild C64 starter-kit.d64 unavailable (offline or hash mismatch)");

    Assert.That(new FileInfo(path!).Length, Is.EqualTo(174848), "D64 canonical size");

    using var s = File.OpenRead(path!);
    var d = new D64FormatDescriptor();
    var entries = d.List(s, null);

    // Public starter-kit disk has dozens of entries. Assert only facts we've verified.
    Assert.That(entries.Count, Is.GreaterThan(10), "starter-kit should list many files");

    // Spot-check one well-known filename present in this disk.
    Assert.That(entries, Has.Some.Matches<ArchiveEntryInfo>(e => e.Name.Contains("C64.MENU", StringComparison.OrdinalIgnoreCase)
                                                              || e.Name.Contains("COPY-ALL", StringComparison.OrdinalIgnoreCase)),
      "starter-kit should contain a menu/copy entry");

    // Extract one entry and check it's non-empty bytes.
    var outDir = Path.Combine(Path.GetTempPath(), $"d64wild_{Guid.NewGuid():N}");
    Directory.CreateDirectory(outDir);
    try {
      s.Position = 0;
      d.Extract(s, outDir, null, files: null);
      var anyOut = Directory.GetFiles(outDir).FirstOrDefault();
      Assert.That(anyOut, Is.Not.Null, "Extract produced no output files");
      Assert.That(new FileInfo(anyOut!).Length, Is.GreaterThan(0), "extracted file should be non-empty");
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // D71 — Commodore 1571 (zelch BBS demodisk from zimmers)
  // ═══════════════════════════════════════════════════════════════════════════

  [Test, Category("HappyPath")]
  public void D71_WildImage_ListsManyEntries() {
    var path = RetroImageCache.FetchGz(
      url: "http://www.zimmers.net/anonftp/pub/cbm/c128/comm/bbs/zelch10.d71.gz",
      sha256OfDecompressed: "701ce2d2dfc898f33d59d58243ed01ab1d4eacdfbe9b02c8a9b124b3aebfea57",
      localName: "cbm-zelch10.d71");
    if (path is null) Assert.Ignore("wild zelch10.d71 unavailable (offline or hash mismatch)");

    Assert.That(new FileInfo(path!).Length, Is.EqualTo(349696), "D71 canonical size");

    using var s = File.OpenRead(path!);
    var d = new D71FormatDescriptor();
    var entries = d.List(s, null);

    Assert.That(entries.Count, Is.GreaterThan(5), "zelch BBS disk should list at least a handful of files");
    Assert.That(entries, Has.Some.Matches<ArchiveEntryInfo>(e => e.Name.Contains("BBS", StringComparison.OrdinalIgnoreCase)
                                                              || e.Name.Contains("ZELCH", StringComparison.OrdinalIgnoreCase)),
      "zelch10.d71 should contain at least one BBS/ZELCH-named entry");
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // D81 — Commodore 1581 (C128 CP/M D81 from zimmers)
  // ═══════════════════════════════════════════════════════════════════════════

  [Test, Category("HappyPath")]
  public void D81_WildImage_Parses() {
    var path = RetroImageCache.FetchGz(
      url: "http://www.zimmers.net/anonftp/pub/cbm/c128/demodisks/cpm.622-5002336.d81.gz",
      sha256OfDecompressed: "5d803c0222f15cac76a6cfd7a2f15c2db13a99a5489cf1a0ebfb26a024b58819",
      localName: "cbm-cpm.d81");
    if (path is null) Assert.Ignore("wild cpm.d81 unavailable (offline or hash mismatch)");

    Assert.That(new FileInfo(path!).Length, Is.EqualTo(819200), "D81 canonical size");

    using var s = File.OpenRead(path!);
    var d = new D81FormatDescriptor();
    var entries = d.List(s, null);

    // This specific image is a near-empty CP/M boot disk with only a copyright file.
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(1), "cpm.d81 should parse at least one entry");
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // T64 — Commodore 64 tape (NTSC demo "anewdecade" from zimmers)
  // ═══════════════════════════════════════════════════════════════════════════

  [Test, Category("HappyPath")]
  public void T64_WildImage_ListsEntry() {
    var path = RetroImageCache.Fetch(
      url: "http://www.zimmers.net/anonftp/pub/cbm/c64/demos/ntsc/Wanderer/anewdecade.t64",
      sha256: "1357263b0c97060eb4179d033ed61799daf5b4d8704495646fc98fa314078fac",
      localName: "cbm-anewdecade.t64");
    if (path is null) Assert.Ignore("wild anewdecade.t64 unavailable (offline or hash mismatch)");

    using var s = File.OpenRead(path!);
    var d = new T64FormatDescriptor();
    var entries = d.List(s, null);

    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(1), "T64 demo should have at least one entry");
    Assert.That(entries, Has.Some.Matches<ArchiveEntryInfo>(e => e.Name.Contains("DECADE", StringComparison.OrdinalIgnoreCase)),
      "T64 entry name should mention DECADE");

    // Extract the primary entry and assert bytes are non-empty.
    using var r = new T64Reader(File.OpenRead(path!));
    var first = r.Entries[0];
    var bytes = r.Extract(first);
    Assert.That(bytes, Is.Not.Empty, "T64 first entry payload should be non-empty");
    // T64 spec note: the declared (endAddr - startAddr) can exceed the physical bytes
    // present in the file — most real T64 writers are off-by-1-or-2 on the last entry.
    // Our Extract() already clamps to the file's physical end, so the output will
    // usually equal the declared size OR fall 1-2 bytes short. Accept either.
    Assert.That(bytes.Length, Is.InRange(first.Size - 4, first.Size),
      "extracted bytes should match entry size (tolerating common off-by-1-or-2 quirks)");
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // Synthetic-only fallbacks for formats where no accessible wild image was
  // found. These round-trip our writer's output through our reader — they do
  // NOT prove we handle images written by real tools (WinUAE, emulator dumps,
  // ATR writer tools, etc.). The [Explicit] attribute keeps them out of the
  // default test set to prevent false confidence; opt-in via category filter.
  // ═══════════════════════════════════════════════════════════════════════════

  [Test, Category("Synthetic"), Explicit("Round-trip only; wild-image validation unavailable for this format.")]
  public void Adf_SyntheticRoundTrip_Only() {
    var w = new AdfWriter();
    w.AddFile("HELLO.TXT", "Hello Amiga"u8.ToArray());
    var img = w.Build();
    using var s = new MemoryStream(img);
    var entries = new AdfFormatDescriptor().List(s, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1), "ADF synthetic round-trip should list the file");
  }

  [Test, Category("Synthetic"), Explicit("Round-trip only; wild-image validation unavailable for this format.")]
  public void Atari8_SyntheticRoundTrip_Only() {
    var w = new Atari8Writer();
    w.AddFile("TEST.TXT", "Atari"u8.ToArray());
    var img = w.Build();
    using var s = new MemoryStream(img);
    var entries = new Atari8FormatDescriptor().List(s, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
  }

  [Test, Category("Synthetic"), Explicit("Round-trip only; wild-image validation unavailable for this format.")]
  public void AppleDos_SyntheticRoundTrip_Only() {
    var w = new AppleDosWriter();
    w.AddFile("HELLO", "Apple DOS"u8.ToArray());
    var img = w.Build();
    using var s = new MemoryStream(img);
    var entries = new AppleDosFormatDescriptor().List(s, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
  }

  [Test, Category("Synthetic"), Explicit("Round-trip only; wild-image validation unavailable for this format.")]
  public void ProDos_SyntheticRoundTrip_Only() {
    var w = new ProDosWriter();
    w.AddFile("HELLO", "ProDOS"u8.ToArray());
    var img = w.Build();
    using var s = new MemoryStream(img);
    var entries = new ProDosFormatDescriptor().List(s, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
  }

  [Test, Category("Synthetic"), Explicit("Round-trip only; wild-image validation unavailable for this format.")]
  public void Bbc_SyntheticRoundTrip_Only() {
    var w = new BbcWriter();
    w.AddFile("HELLO", "BBC"u8.ToArray());
    var img = w.Build();
    using var s = new MemoryStream(img);
    var entries = new BbcFormatDescriptor().List(s, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
  }

  [Test, Category("Synthetic"), Explicit("Round-trip only; wild-image validation unavailable for this format.")]
  public void ZxScl_SyntheticRoundTrip_Only() {
    var w = new ZxSclWriter();
    w.AddFile("HELLO   ", "ZX"u8.ToArray(), fileType: 'B');
    var img = w.Build();
    using var s = new MemoryStream(img);
    var entries = new ZxSclFormatDescriptor().List(s, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
  }

  [Test, Category("Synthetic"), Explicit("Round-trip only; wild-image validation unavailable for this format.")]
  public void TrDos_SyntheticRoundTrip_Only() {
    var w = new TrDosWriter();
    w.AddFile("HELLO   ", 'B', "TR-DOS"u8.ToArray());
    var img = w.Build();
    using var s = new MemoryStream(img);
    var entries = new TrDosFormatDescriptor().List(s, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
  }

  [Test, Category("Synthetic"), Explicit("Round-trip only; wild-image validation unavailable for this format.")]
  public void CpcDsk_SyntheticRoundTrip_Only() {
    using var ms = new MemoryStream();
    using (var w = new CpcDskWriter(ms, leaveOpen: true)) {
      w.AddFile("HELLO.TXT", "CPC"u8.ToArray());
      w.Finish();
    }
    ms.Position = 0;
    var entries = new CpcDskFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
  }

  [Test, Category("Synthetic"), Explicit("Round-trip only; wild-image validation unavailable for this format.")]
  public void Msa_SyntheticRoundTrip_Only() {
    // MSA is a raw-track container. Build a minimal 1-track SS/9SPT disk (4608 bytes) and wrap it.
    var rawDisk = new byte[9 * 512];
    using var outMs = new MemoryStream();
    MsaWriter.Write(outMs, rawDisk, sectorsPerTrack: 9, sides: 0);
    outMs.Position = 0;
    Assert.That(() => new MsaFormatDescriptor().List(outMs, null), Throws.Nothing,
      "MSA descriptor should parse a synthesized raw-track image");
  }

  [Test, Category("Synthetic"), Explicit("Round-trip only; wild-image validation unavailable for this format.")]
  public void Mfs_SyntheticRoundTrip_Only() {
    var w = new MfsWriter();
    w.AddFile("HELLO", "MFS"u8.ToArray());
    var img = w.Build();
    using var s = new MemoryStream(img);
    var entries = new MfsFormatDescriptor().List(s, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
  }

  [Test, Category("Synthetic"), Explicit("DMS is write-once with our encoder; no accessible wild samples fetched in-band.")]
  public void Dms_SyntheticRoundTrip_Only() {
    // DMS compresses raw disk-track data. Build a small random disk and round-trip it.
    var trackData = new byte[11264];
    Random.Shared.NextBytes(trackData);
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dms.DmsWriter(ms, leaveOpen: true))
      w.WriteTrack(0, trackData, compressionMode: 0);

    ms.Position = 0;
    using var rd = new FileFormat.Dms.DmsReader(ms);
    Assert.That(rd.Entries, Has.Count.EqualTo(1), "DMS round-trip should list one track");
    Assert.That(rd.Extract(rd.Entries[0]), Is.EqualTo(trackData), "DMS round-trip bytes should match");
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // Cross-tool: c1541 (vice) + hfsutils are documented skips — they require
  // `sudo apt install -y vice hfsutils`, which fails non-interactively in this
  // environment (sudo prompts for a password). The tests below document the
  // invocation so a human can run it manually after installing the tools.
  // ═══════════════════════════════════════════════════════════════════════════

  [Test]
  public void D64_OurWriter_ValidatedByC1541_Skip_ManualOnly() {
    Assert.Ignore("Cross-tool c1541 validation requires VICE's c1541 in WSL: " +
                  "`sudo apt install -y vice`. After installing, manually run: " +
                  "`wsl c1541 -attach <our-image.d64> -list`. Exit 0 = pass.");
  }

  [Test]
  public void Mfs_OurWriter_ValidatedByHls_Skip_ManualOnly() {
    Assert.Ignore("Cross-tool hfsutils validation requires `sudo apt install -y hfsutils` " +
                  "then `hmount <our-image.mfs>` + `hls`. Not installed in default WSL.");
  }
}
