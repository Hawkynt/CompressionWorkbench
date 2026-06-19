using System.Buffers.Binary;
using System.Text;
using FileFormat.EaseUs;

namespace Compression.Tests.EaseUs;

/// <summary>
/// Self-round-trip acceptance gate for <see cref="EaseUsWriter"/>. The
/// writer emits a <c>.pbd</c> container whose header / body / trailer
/// framing matches the on-disk shape recovered from the EaseUS image
/// engine (<c>ImgFile.dll</c>) and pinned in
/// <see cref="EaseUsContainerIndex"/>. These tests confirm:
/// <list type="bullet">
///   <item><description>
///     The output passes the strict-form 0x4E8 header + 0xC0 trailer
///     validators through our own <see cref="EaseUsReader"/>.
///   </description></item>
///   <item><description>
///     Every stored file's payload round-trips byte-identical via the
///     body zlib substreams (manifest + per-file chunk).
///   </description></item>
///   <item><description>
///     Edge cases — empty files, nested paths, high-entropy /
///     hard-to-compress content — survive the round trip.
///   </description></item>
/// </list>
/// <para>
/// The writer is NOT yet advertised via <c>CanCreate</c>: whether the
/// vendor application restores this exact byte layout requires a
/// human-run GUI restore test against the produced corpus.pbd.
/// </para>
/// </summary>
[TestFixture]
public class EaseUsWriterTests {

  private static IReadOnlyList<EaseUsWriter.FileEntry> SampleFiles() => [
    new("readme.txt", Encoding.ASCII.GetBytes("hello easeus pbd writer")),
    new("subdir/nested.txt", Encoding.ASCII.GetBytes("nested content here")),
    new("empty.dat", []),
    new("binary.bin", Enumerable.Range(0, 1024).Select(i => (byte)(i & 0xFF)).ToArray()),
  ];

  // ---------------------------------------------------------------------
  // Container framing — strict-form header + trailer validation.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Build_ProducesStrictFormValidHeaderAndTrailer() {
    var pbd = EaseUsWriter.Build(SampleFiles(), sourcePath: "C:\\src\\corpus");
    using var ms = new MemoryStream(pbd);
    var r = new EaseUsReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.ValidHeader, Is.True);
      Assert.That(r.MagicVariant, Is.EqualTo("IMGF"));
      Assert.That(r.HeaderBlockFullyValidated, Is.True, "0x4E8 header block must validate strict-form.");
      Assert.That(r.TrailerBlockFullyValidated, Is.True, "0xC0 trailer block must validate strict-form.");
      Assert.That(r.TrailerImgfPresent, Is.True);
      Assert.That(r.TrailingFfPadding, Is.EqualTo(EaseUsWriter.DefaultTrailingFfPadding));
    });
  }

  [Test, Category("HappyPath")]
  public void Build_HeaderWordsCarryWriterSideConstants() {
    var pbd = EaseUsWriter.Build(SampleFiles());
    var sz = BinaryPrimitives.ReadUInt32LittleEndian(pbd.AsSpan(EaseUsContainerIndex.HeaderSizeFieldOffset, 4));
    var ver = BinaryPrimitives.ReadUInt32LittleEndian(pbd.AsSpan(EaseUsContainerIndex.HeaderVersionFieldOffset, 4));
    Assert.Multiple(() => {
      Assert.That(pbd.AsSpan(0, 4).SequenceEqual("IMGF"u8), Is.True);
      Assert.That(sz, Is.EqualTo(EaseUsContainerIndex.HeaderSizeFieldExpectedValue));
      Assert.That(ver, Is.EqualTo(EaseUsContainerIndex.HeaderVersionFieldExpectedValue));
    });
  }

  [Test, Category("HappyPath")]
  public void Build_EmbedsSourcePathAsUtf16Le() {
    const string src = "G:\\backup\\corpus";
    var pbd = EaseUsWriter.Build(SampleFiles(), sourcePath: src);
    using var ms = new MemoryStream(pbd);
    var r = new EaseUsReader(ms);
    Assert.That(r.EmbeddedSourcePath, Is.EqualTo(src));
    Assert.That(r.EmbeddedSourcePathOffset, Is.EqualTo(EaseUsReader.HeaderSize));
  }

  // ---------------------------------------------------------------------
  // Body round-trip — every file payload recovered byte-identical.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Build_BodyZlibSubstreams_RoundTripEveryPayload() {
    var files = SampleFiles();
    var pbd = EaseUsWriter.Build(files);
    var recovered = RecoverFiles(pbd);

    Assert.That(recovered, Has.Count.EqualTo(files.Count));
    for (var i = 0; i < files.Count; i++) {
      Assert.That(recovered[i].path, Is.EqualTo(files[i].RelativePath.Replace('\\', '/')));
      Assert.That(recovered[i].content, Is.EqualTo(files[i].Content),
        $"File #{i} ({files[i].RelativePath}) must round-trip byte-identical.");
    }
  }

  [Test, Category("Boundary")]
  public void Build_HandlesEmptyFileList() {
    var pbd = EaseUsWriter.Build([]);
    using var ms = new MemoryStream(pbd);
    var r = new EaseUsReader(ms);
    Assert.That(r.HeaderBlockFullyValidated, Is.True);
    Assert.That(r.TrailerBlockFullyValidated, Is.True);
    // Only the manifest substream is present; it inflates to an empty manifest.
    Assert.That(r.ConfirmedZlibChunkCount, Is.GreaterThanOrEqualTo(1));
  }

  [Test, Category("Boundary")]
  public void Build_HandlesZeroLengthFileContent() {
    var files = new List<EaseUsWriter.FileEntry> { new("empty.dat", []) };
    var pbd = EaseUsWriter.Build(files);
    var recovered = RecoverFiles(pbd);
    Assert.That(recovered, Has.Count.EqualTo(1));
    Assert.That(recovered[0].content, Is.Empty);
  }

  [Test, Category("Boundary")]
  public void Build_HandlesHighEntropyContent() {
    var rnd = new Random(1234);
    var blob = new byte[256 * 1024];
    rnd.NextBytes(blob);
    var files = new List<EaseUsWriter.FileEntry> { new("incompressible.bin", blob) };
    var pbd = EaseUsWriter.Build(files);
    var recovered = RecoverFiles(pbd);
    Assert.That(recovered[0].content, Is.EqualTo(blob));
  }

  [Test, Category("Sad")]
  public void Build_ThrowsOnNullFiles() {
    Assert.Throws<ArgumentNullException>(() => EaseUsWriter.Build(null!));
  }

  // ---------------------------------------------------------------------
  // BuildFromDirectory — directory tree round trip.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void BuildFromDirectory_RoundTripsTree() {
    var tmp = Path.Combine(Path.GetTempPath(), "easeus_writer_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(Path.Combine(tmp, "sub", "deep"));
    try {
      File.WriteAllText(Path.Combine(tmp, "a.txt"), "alpha");
      File.WriteAllText(Path.Combine(tmp, "sub", "b.txt"), "bravo nested");
      File.WriteAllBytes(Path.Combine(tmp, "sub", "deep", "c.bin"), [0, 1, 2, 3, 255]);
      File.WriteAllBytes(Path.Combine(tmp, "empty.dat"), []);

      var pbd = EaseUsWriter.BuildFromDirectory(tmp);
      var recovered = RecoverFiles(pbd).ToDictionary(x => x.path, x => x.content);

      Assert.Multiple(() => {
        Assert.That(recovered.ContainsKey("a.txt"), Is.True);
        Assert.That(Encoding.UTF8.GetString(recovered["a.txt"]), Is.EqualTo("alpha"));
        Assert.That(Encoding.UTF8.GetString(recovered["sub/b.txt"]), Is.EqualTo("bravo nested"));
        Assert.That(recovered["sub/deep/c.bin"], Is.EqualTo(new byte[] { 0, 1, 2, 3, 255 }));
        Assert.That(recovered["empty.dat"], Is.Empty);
      });
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best-effort */ }
    }
  }

  // ---------------------------------------------------------------------
  // Helpers — walk the writer output back into (path, content) tuples by
  // inflating the body substreams and parsing the manifest. This mirrors
  // exactly how our own reader's zlib scanner sees the body.
  // ---------------------------------------------------------------------

  private static List<(string path, byte[] content)> RecoverFiles(byte[] pbd) {
    // The manifest substream is the first body substream, at the header
    // block boundary. Inflate it deterministically via the scanner's
    // single-stream TryInflate, then resolve each file's payload by the
    // exact compressed offset + length the manifest records.
    var manifestChunk = EaseUsZlibScanner.TryInflate(pbd, EaseUsContainerIndex.HeaderBlockSize, maxRetainedPayloadBytes: int.MaxValue);
    Assert.That(manifestChunk.InflateStatus, Is.EqualTo(EaseUsChunkInflateStatus.Inflated),
      "Manifest substream must inflate cleanly at the header block boundary.");

    // Absolute base of the first payload substream = header block size +
    // the manifest substream's compressed length. Manifest offsets are
    // relative to this base.
    var payloadBase = EaseUsContainerIndex.HeaderBlockSize + (int)manifestChunk.CompressedLength;

    var manifestText = Encoding.UTF8.GetString(manifestChunk.Payload);
    var rows = manifestText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var result = new List<(string, byte[])>();
    foreach (var row in rows) {
      var cols = row.Split('\t');
      var relPath = cols[0];
      var size = long.Parse(cols[1]);
      var compOffset = payloadBase + int.Parse(cols[2]);
      var compLength = int.Parse(cols[3]);

      var c = EaseUsZlibScanner.TryInflate(pbd, compOffset, maxRetainedPayloadBytes: int.MaxValue);
      Assert.That(c.InflateStatus, Is.EqualTo(EaseUsChunkInflateStatus.Inflated),
        $"Payload substream for {relPath} at offset {compOffset} must inflate.");
      Assert.That(c.CompressedLength, Is.EqualTo(compLength),
        $"Manifest compressed length for {relPath} must match the substream.");
      Assert.That(c.Payload.Length, Is.EqualTo(size),
        $"Manifest size for {relPath} must match its substream payload length.");
      result.Add((relPath, c.Payload));
    }
    return result;
  }
}
