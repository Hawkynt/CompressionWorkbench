#pragma warning disable CS1591
using System.Text;
using FileFormat.Ewf;

namespace Compression.Tests.Ewf;

/// <summary>
/// Conformance gates for the EnCase Expert Witness Format (EWF / .E01) writer,
/// run against the reference <c>libewf</c> tools (ewfverify / ewfinfo /
/// ewfexport / ewfacquire) installed in WSL.
///
/// <para><b>Gates.</b></para>
/// <list type="number">
///   <item><description><b>Self round-trip</b> (no external tool): our writer's
///   image read back by <see cref="EwfReader"/> exposes the expected section
///   chain, every descriptor + payload Adler-32 validates, and the reconstructed
///   media is byte-identical to the input.</description></item>
///   <item><description><b>Forward gate</b> (<c>ewfverify</c>): libewf must accept
///   our image and report the stored MD5 matching the calculated MD5.</description></item>
///   <item><description><b>ewfinfo gate</b>: libewf dumps our acquisition header +
///   media geometry without error.</description></item>
///   <item><description><b>ewfexport gate</b>: libewf reconstructs the raw image
///   from our .E01 byte-for-byte.</description></item>
///   <item><description><b>Reverse gate</b> (<c>ewfacquire</c>): libewf acquires a
///   raw file into .E01 and our <see cref="EwfReader"/> reads it back.</description></item>
/// </list>
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
[Category("EwfExternal")]
public class EwfExternalConformanceTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ewf_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Capability probes ───────────────────────────────────────────────

  private static bool EwfVerifyAvailable =>
    FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("ewfverify");
  private static bool EwfInfoAvailable =>
    FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("ewfinfo");
  private static bool EwfExportAvailable =>
    FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("ewfexport");
  private static bool EwfAcquireAvailable =>
    FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("ewfacquire");

  // ── Test media ──────────────────────────────────────────────────────

  /// <summary>Deterministic pseudo-random media of the given length (sector-aligned).</summary>
  private static byte[] Media(int sectors) {
    var data = new byte[sectors * EwfWriter.BytesPerSector];
    // Deterministic LCG so failures reproduce; non-trivial so a stub writer
    // cannot pass by emitting zeros.
    uint state = 0x1234_5678;
    for (var i = 0; i < data.Length; i++) {
      state = state * 1664525u + 1013904223u;
      data[i] = (byte)(state >> 24);
    }
    return data;
  }

  // ── Gate 1: self round-trip (always runnable) ───────────────────────

  [Test]
  public void SelfRoundTrip_OurWriter_ReadBackIsByteIdentical() {
    // 5 chunks-worth + a partial chunk to exercise the tail path.
    var media = Media(sectors: 64 * 5 + 17);
    var image = new EwfWriter {
      CaseNumber = "C1", EvidenceNumber = "E1", Description = "self round-trip",
      ExaminerName = "cwb", Notes = "n",
    }.Build(media);

    var img = EwfReader.Read(image);

    Assert.Multiple(() => {
      Assert.That(img.IsLogical, Is.False, "EVF physical image expected");
      Assert.That(img.SegmentNumber, Is.EqualTo((ushort)1));
      var types = img.Sections.Select(s => s.Type).ToArray();
      Assert.That(types, Does.Contain("volume"));
      Assert.That(types, Does.Contain("sectors"));
      Assert.That(types, Does.Contain("table"));
      Assert.That(types, Does.Contain("hash"));
      Assert.That(types[^1], Is.EqualTo("done"), "chain must terminate with done");
    });

    var reconstructed = ReconstructMedia(image);
    // Media is padded up to a full sector; here it already is sector-aligned.
    Assert.That(reconstructed, Is.EqualTo(media),
      "reconstructed media must be byte-identical to the input");
  }

  [Test]
  public void SelfRoundTrip_Compressed_ReadBackIsByteIdentical() {
    // Highly compressible media so chunk compression actually engages.
    var media = new byte[64 * 512 * 3 + 1000];
    for (var i = 0; i < media.Length; i++) media[i] = (byte)(i % 7);
    var image = new EwfWriter { CompressChunks = true }.Build(media);

    var reconstructed = ReconstructMedia(image);
    var padded = new byte[(media.Length + 511) / 512 * 512];
    Array.Copy(media, padded, media.Length);
    Assert.That(reconstructed, Is.EqualTo(padded));
  }

  // ── Gate 2: ewfverify forward gate ──────────────────────────────────

  [Test]
  public void ForwardGate_EwfVerify_AcceptsOurImage_StoredChunks() {
    if (!EwfVerifyAvailable)
      Assert.Ignore("ewfverify not available in WSL (install ewf-tools).");

    var media = Media(sectors: 64 * 7 + 33);
    var winImg = Path.Combine(this._tmpDir, "ours.E01");
    File.WriteAllBytes(winImg, new EwfWriter().Build(media));

    var wsl = FsInteropToolbox.WinToWsl(winImg);
    var r = FsInteropToolbox.RunWsl($"ewfverify {wsl} 2>&1");

    var output = r.StdOut + r.StdErr;
    Assert.Multiple(() => {
      Assert.That(r.ExitCode, Is.EqualTo(0),
        $"ewfverify must accept our image.\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
      Assert.That(output, Does.Contain("SUCCESS"),
        $"ewfverify must report SUCCESS.\n{output}");
      // The stored MD5 (from our hash section) must equal the value libewf
      // recomputes over the reconstructed media.
      var stored = ExtractHash(output, "MD5 hash stored in file");
      var calculated = ExtractHash(output, "MD5 hash calculated over data");
      Assert.That(stored, Is.Not.Empty.And.EqualTo(calculated).IgnoreCase,
        $"stored MD5 must match calculated MD5.\n{output}");
    });
  }

  [Test]
  public void ForwardGate_EwfVerify_AcceptsOurImage_CompressedChunks() {
    if (!EwfVerifyAvailable)
      Assert.Ignore("ewfverify not available in WSL (install ewf-tools).");

    var media = new byte[64 * 512 * 4 + 2048];
    for (var i = 0; i < media.Length; i++) media[i] = (byte)(i % 11);
    var winImg = Path.Combine(this._tmpDir, "ours_z.E01");
    File.WriteAllBytes(winImg, new EwfWriter { CompressChunks = true }.Build(media));

    var wsl = FsInteropToolbox.WinToWsl(winImg);
    var r = FsInteropToolbox.RunWsl($"ewfverify {wsl} 2>&1");

    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"ewfverify must accept our zlib-compressed image.\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }

  [Test]
  public void ForwardGate_EwfVerify_AcceptsDescriptorCreatedImage() {
    if (!EwfVerifyAvailable)
      Assert.Ignore("ewfverify not available in WSL (install ewf-tools).");

    // Drive the registry descriptor's CanCreate path end-to-end.
    var media = Media(sectors: 64 * 5 + 7);
    var winImg = Path.Combine(this._tmpDir, "desc.E01");
    using (var fs = File.Create(winImg)) {
      var desc = new EwfFormatDescriptor();
      desc.Create(fs, [global::Compression.Registry.ArchiveInputInfo.InMemory("disk.dd", media)],
        new global::Compression.Registry.FormatCreateOptions());
    }

    var wsl = FsInteropToolbox.WinToWsl(winImg);
    var r = FsInteropToolbox.RunWsl($"ewfverify {wsl} 2>&1");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"ewfverify must accept the descriptor-created image.\n{r.StdOut}\n{r.StdErr}");
    Assert.That(r.StdOut + r.StdErr, Does.Contain("SUCCESS"), r.StdOut + r.StdErr);
  }

  // ── Gate 3: ewfinfo dumps our header ────────────────────────────────

  [Test]
  public void InfoGate_EwfInfo_DumpsOurMetadata() {
    if (!EwfInfoAvailable)
      Assert.Ignore("ewfinfo not available in WSL (install ewf-tools).");

    var media = Media(sectors: 64 * 3);
    var winImg = Path.Combine(this._tmpDir, "info.E01");
    File.WriteAllBytes(winImg, new EwfWriter {
      CaseNumber = "CASE42", EvidenceNumber = "EV1", ExaminerName = "examiner",
    }.Build(media));

    var wsl = FsInteropToolbox.WinToWsl(winImg);
    var r = FsInteropToolbox.RunWsl($"ewfinfo {wsl} 2>&1");

    Assert.Multiple(() => {
      Assert.That(r.ExitCode, Is.EqualTo(0),
        $"ewfinfo must parse our image.\n{r.StdOut}\n{r.StdErr}");
      Assert.That(r.StdOut, Does.Contain("bytes per sector").IgnoreCase
        .Or.Contain("Bytes per sector").Or.Contain("512"),
        $"ewfinfo should report media geometry.\n{r.StdOut}");
    });
  }

  // ── Gate 4: ewfexport reconstructs the raw image byte-for-byte ──────

  [Test]
  public void ExportGate_EwfExport_ReconstructsRawByteForByte() {
    if (!EwfExportAvailable)
      Assert.Ignore("ewfexport not available in WSL (install ewf-tools).");

    var media = Media(sectors: 64 * 6 + 9);
    var winImg = Path.Combine(this._tmpDir, "exp.E01");
    File.WriteAllBytes(winImg, new EwfWriter().Build(media));

    var wslImg = FsInteropToolbox.WinToWsl(winImg);
    var winRaw = Path.Combine(this._tmpDir, "exported.raw");
    var wslRaw = FsInteropToolbox.WinToWsl(winRaw);

    // ewfexport -u (unattended), raw format, to the given target basename.
    var r = FsInteropToolbox.RunWsl($"ewfexport -u -f raw -t {wslRaw} {wslImg} 2>&1");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"ewfexport must reconstruct our image.\n{r.StdOut}\n{r.StdErr}");

    // ewfexport writes "<target>.raw" for a single-segment raw export.
    var produced = Directory.GetFiles(this._tmpDir, "exported.raw*")
      .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    Assert.That(produced, Is.Not.Null,
      $"ewfexport produced no raw output.\n{r.StdOut}\n{r.StdErr}");

    var exported = File.ReadAllBytes(produced!);
    Assert.That(exported, Is.EqualTo(media),
      "ewfexport-reconstructed media must be byte-identical to the input");
  }

  // ── Gate 5: reverse gate — read a real ewfacquire image ─────────────

  [Test]
  public void ReverseGate_ReadsRealEwfacquireImage() {
    if (!EwfAcquireAvailable)
      Assert.Ignore("ewfacquire not available in WSL (install ewf-tools).");

    var media = Media(sectors: 64 * 4 + 5);
    var winRaw = Path.Combine(this._tmpDir, "src.dd");
    File.WriteAllBytes(winRaw, media);
    var wslRaw = FsInteropToolbox.WinToWsl(winRaw);

    var winOut = Path.Combine(this._tmpDir, "acq");          // ewfacquire appends .E01
    var wslOut = FsInteropToolbox.WinToWsl(winOut);
    var r = FsInteropToolbox.RunWsl(
      $"ewfacquire -u -c none -f encase6 -S 1.4EiB -t {wslOut} " +
      $"-C c -D d -e e -E ev -N n -l - {wslRaw} 2>&1");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"ewfacquire must produce an image.\n{r.StdOut}\n{r.StdErr}");

    var produced = Path.Combine(this._tmpDir, "acq.E01");
    Assert.That(File.Exists(produced), Is.True,
      $"ewfacquire did not write acq.E01.\n{r.StdOut}");

    var img = EwfReader.Read(File.ReadAllBytes(produced));
    Assert.Multiple(() => {
      Assert.That(img.IsLogical, Is.False);
      Assert.That(img.Sections.Select(s => s.Type), Does.Contain("volume"));
      Assert.That(img.Sections.Select(s => s.Type), Does.Contain("sectors"));
      Assert.That(img.Sections[^1].Type, Is.EqualTo("done"));
    });
  }

  /// <summary>Pulls the hex hash following a labelled <c>ewfverify</c> line.</summary>
  private static string ExtractHash(string output, string label) {
    foreach (var line in output.Split('\n')) {
      var idx = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
      if (idx < 0) continue;
      var rest = line[(idx + label.Length)..];
      var hex = new string(rest.Where(Uri.IsHexDigit).ToArray());
      if (hex.Length >= 32) return hex;
    }
    return "";
  }

  // ── Reconstruction helper (our reader → raw media) ──────────────────

  /// <summary>
  /// Reconstructs the raw media from one of our .E01 images by walking the table
  /// section and decoding each chunk (stored: strip trailing Adler-32; compressed:
  /// zlib-inflate). Validates our writer is internally consistent without relying
  /// on libewf.
  /// </summary>
  private static byte[] ReconstructMedia(byte[] image) {
    var img = EwfReader.Read(image);

    var volume = img.Sections.First(s => s.Type is "volume" or "disk");
    var chunkCount = (int)BitConverter.ToUInt32(volume.Payload, 4);
    var totalSectors = BitConverter.ToUInt32(volume.Payload, 16);
    var sectorsPerChunk = BitConverter.ToUInt32(volume.Payload, 8);
    var bytesPerSector = BitConverter.ToUInt32(volume.Payload, 12);
    var totalBytes = (long)totalSectors * bytesPerSector;
    var fullChunkBytes = (int)(sectorsPerChunk * bytesPerSector);

    var table = img.Sections.First(s => s.Type == "table");
    var entryCount = (int)BitConverter.ToUInt32(table.Payload, 0);
    var baseOffset = BitConverter.ToUInt64(table.Payload, 8);
    Assert.That(entryCount, Is.EqualTo(chunkCount), "table entry count must match chunk count");

    // Each entry is an absolute file offset = baseOffset + entry (low 31 bits);
    // MSB flags a compressed chunk.
    var entries = new (long Offset, bool Compressed)[entryCount];
    for (var i = 0; i < entryCount; i++) {
      var raw = BitConverter.ToUInt32(table.Payload, 24 + i * 4);
      entries[i] = ((long)(baseOffset + (raw & 0x7FFFFFFF)), (raw & 0x80000000) != 0);
    }

    using var output = new MemoryStream();
    for (var i = 0; i < entryCount; i++) {
      var start = entries[i].Offset;
      // The end of a chunk's encoded bytes is the next entry's offset, or the
      // start of the table section for the last chunk.
      var end = i + 1 < entryCount ? entries[i + 1].Offset : table.DescriptorOffset;
      var encoded = image.AsSpan((int)start, (int)(end - start));

      if (entries[i].Compressed) {
        var inflated = FileFormat.Zlib.ZlibStream.Decompress(encoded);
        output.Write(inflated);
      } else {
        // Stored chunk: drop the trailing 4-byte Adler-32.
        output.Write(encoded[..^4]);
      }
    }

    var result = output.ToArray();
    // Trim padding back to the recorded media length.
    if (result.Length > totalBytes) Array.Resize(ref result, (int)totalBytes);
    return result;
  }
}
