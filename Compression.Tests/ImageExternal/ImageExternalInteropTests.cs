#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;
using CompressionWorkbench.FileFormat.Ico;
using FileFormat.Avif;
using FileFormat.Jp2;
using FileFormat.Webp;

namespace Compression.Tests.ImageExternal;

/// <summary>
/// Cross-validates our image-format readers and writers against reference
/// tools available inside WSL Ubuntu (ImageMagick <c>convert</c>/<c>identify</c>,
/// <c>exiftool</c>, libwebp <c>dwebp</c>/<c>cwebp</c>, libavif <c>avifdec</c>).
/// <para>
/// Two directions are exercised:
/// </para>
/// <list type="number">
///   <item><b>Tool output → our reader</b>: ImageMagick mints a tiny 4x4
///   reference image; our descriptor's <c>List</c>/<c>Extract</c> must parse it
///   and report matching dimensions.</item>
///   <item><b>Our output → tool validates</b>: we emit an image (ICO via the
///   native writer; an extended WebP via <c>cwebp</c> seeded from our pixels)
///   and <c>identify</c>/<c>exiftool</c> must report the right format and size.</item>
/// </list>
/// <para>
/// Every test is gated on <see cref="FsInteropToolbox.WslAvailable"/> plus the
/// specific tool it needs, skipping via <see cref="Assert.Ignore(string)"/> with
/// an actionable hint when the tool is absent. All reference images are minted
/// in-test into the per-test temp directory and kept at 4x4 so the suite is fast.
/// </para>
/// </summary>
[TestFixture]
[Category("ImageExternalInterop")]
public class ImageExternalInteropTests {
  private string _tmpDir = null!;
  private string _wslTmpDir = null!;

  [SetUp]
  public void Setup() {
    FormatRegistration.EnsureInitialized();
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_imginterop_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
    this._wslTmpDir = FsInteropToolbox.WinToWsl(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Gate helpers ───────────────────────────────────────────────────

  private static void RequireTool(string tool) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not available. Install WSL Ubuntu and the imaging packages "
                    + "(imagemagick exiftool webp libavif-bin) to run image interop tests.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"WSL tool '{tool}' not installed. Install via "
                    + "`sudo apt install -y imagemagick exiftool webp libavif-bin`.");
  }

  /// <summary>
  /// Mints a 4x4 solid-colour reference image of the given file via ImageMagick
  /// <c>convert</c> and returns the Windows path. Fails the test (not skip) when
  /// generation fails, so a broken tool surfaces loudly rather than silently.
  /// </summary>
  private string MakeReference(string fileName, string convertArgs) {
    var winPath = Path.Combine(this._tmpDir, fileName);
    var r = FsInteropToolbox.RunWsl($"convert {convertArgs} {this.WslPathFor(fileName)}");
    Assert.That(File.Exists(winPath), Is.True,
      $"ImageMagick failed to mint {fileName}:\nstdout:{r.StdOut}\nstderr:{r.StdErr}");
    return winPath;
  }

  /// <summary>
  /// Splices <paramref name="fileName"/> into the (already single-quoted) WSL temp
  /// path so the result is one literal quoted POSIX path bash can consume.
  /// </summary>
  private string WslPathFor(string fileName)
    => this._wslTmpDir.EndsWith('\'')
      ? this._wslTmpDir[..^1] + "/" + fileName + "'"
      : this._wslTmpDir + "/" + fileName;

  // ── Direction 2: tool output → our reader ───────────────────────────

  [Test]
  public void Png_ImageMagickReference_ParsedByOurReader() {
    RequireTool("convert");
    var png = this.MakeReference("ref.png", "-size 4x4 xc:red");

    var desc = FormatRegistry.All.FirstOrDefault(d => d.Id == "Png") as IArchiveFormatOperations;
    if (desc is null)
      Assert.Ignore("Png descriptor not registered — sibling PngCrushCS absent at ../../PNGCrushCS.");

    // PngFormatDescriptor decomposes the chunk stream and parses IHDR into
    // metadata.ini (width/height as `key = value`).
    var meta = this.ExtractMetadataIni("Png", png, desc!);
    Assert.That(meta, Is.Not.Null, "PNG reader produced no metadata.ini");
    Assert.That(meta!, Does.Match(@"width\s*=\s*4"), $"PNG width mismatch:\n{meta}");
    Assert.That(meta!, Does.Match(@"height\s*=\s*4"), $"PNG height mismatch:\n{meta}");
  }

  [Test]
  public void Gif_ImageMagickReference_ParsedByOurReader() {
    RequireTool("convert");
    var gif = this.MakeReference("ref.gif", "-size 4x4 xc:blue");

    // Native GIF decoder — sibling-independent path: decode straight to frames.
    using var fs = File.OpenRead(gif);
    using var ms = new MemoryStream();
    fs.CopyTo(ms);
    var frames = new FileFormat.Gif.GifPixelDecoder().Decode(ms.ToArray());
    Assert.That(frames, Is.Not.Empty, "GIF decoder returned no frames");
    Assert.That(frames[0].Width, Is.EqualTo(4), "GIF width mismatch");
    Assert.That(frames[0].Height, Is.EqualTo(4), "GIF height mismatch");
  }

  [Test]
  public void Tiff_ImageMagickReference_ParsedByOurReader() {
    RequireTool("convert");
    var tiff = this.MakeReference("ref.tif", "-size 4x4 xc:green");

    var desc = FormatRegistry.All.FirstOrDefault(d => d.Id == "Tiff") as IArchiveFormatOperations;
    if (desc is null)
      Assert.Ignore("Tiff descriptor not registered — sibling PngCrushCS absent at ../../PNGCrushCS.");

    // TiffFormatDescriptor re-emits each IFD as a self-contained single-page
    // TIFF (pages/page_NNN.tif). Extract it and let identify confirm 4x4 —
    // proves our reader produced a structurally valid page the tool agrees on.
    var outDir = Path.Combine(this._tmpDir, "x_Tiff");
    Directory.CreateDirectory(outDir);
    using (var fs = File.OpenRead(tiff))
      desc!.Extract(fs, outDir, null, null);

    var page = Directory.EnumerateFiles(outDir, "page_000.tif", SearchOption.AllDirectories).FirstOrDefault();
    Assert.That(page, Is.Not.Null, "TIFF reader emitted no page_000.tif");

    // Copy the extracted page next to our WSL temp root so identify can read it.
    var pageName = "tiff_page0.tif";
    File.Copy(page!, Path.Combine(this._tmpDir, pageName), true);
    var id = FsInteropToolbox.RunWsl($"identify -format '%wx%h' {this.WslPathFor(pageName)}");
    Assert.That(id.ExitCode, Is.EqualTo(0), $"identify rejected our extracted TIFF page:\n{id.StdErr}");
    Assert.That(id.StdOut, Does.Contain("4x4"), $"identify dims on our TIFF page mismatch:\n{id.StdOut}");
  }

  [Test]
  public void Bmp_OurIcoWriter_EmbedsBmpValidatedByIdentify() {
    RequireTool("identify");
    // There is no standalone BMP descriptor in CompressionWorkbench — BMP is
    // exercised through IcoWriter, which ingests a BMP frame and DIB-packs it
    // into an .ico. ImageMagick mints the BMP; identify confirms the resulting
    // container is a 4x4 ICO, proving our BMP→DIB path is structurally sound.
    var bmp = this.MakeReference("ref.bmp", "-size 4x4 xc:navy -define bmp:format=bmp3");
    var ico = IcoWriter.BuildIco([new IcoWriter.Image(File.ReadAllBytes(bmp))]);
    var icoPath = Path.Combine(this._tmpDir, "frombmp.ico");
    File.WriteAllBytes(icoPath, ico);

    var r = FsInteropToolbox.RunWsl($"identify -verbose {this.WslPathFor("frombmp.ico")}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"identify rejected our BMP-sourced ICO:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("ICO").IgnoreCase, $"not recognised as ICO:\n{r.StdOut}");
    Assert.That(r.StdOut, Does.Match(@"Geometry:\s*4x4"), $"geometry mismatch:\n{r.StdOut}");
  }

  [Test]
  public void Webp_ImageMagickReference_ParsedByOurReader() {
    RequireTool("cwebp");
    // A plain `convert x.webp` yields a bare VP8 still (no VP8X) so our reader
    // surfaces only the codec, not dimensions. Force the extended (VP8X)
    // container by feeding cwebp an image with an alpha channel — that path
    // emits the canvas width/height our reader reads from VP8X.
    this.MakeReference("seed.png", "-size 4x4 xc:\"rgba(255,0,0,0.5)\"");
    var r = FsInteropToolbox.RunWsl($"cwebp -quiet {this.WslPathFor("seed.png")} -o {this.WslPathFor("ref.webp")}");
    var webp = Path.Combine(this._tmpDir, "ref.webp");
    Assert.That(File.Exists(webp), Is.True, $"cwebp failed:\n{r.StdErr}");

    var meta = ExtractMetadataIni("Webp", webp, new WebpFormatDescriptor());
    Assert.That(meta, Is.Not.Null, "WebP reader produced no metadata.ini");
    Assert.That(meta!, Does.Contain("parse_status=ok"), "WebP not parsed");
    Assert.That(meta!, Does.Contain("width=4"), $"WebP width mismatch:\n{meta}");
    Assert.That(meta!, Does.Contain("height=4"), $"WebP height mismatch:\n{meta}");
  }

  [Test]
  public void Jp2_ImageMagickReference_ParsedByOurReader() {
    RequireTool("convert");
    var jp2 = this.MakeReference("ref.jp2", "-size 4x4 xc:red");
    var meta = ExtractMetadataIni("Jp2", jp2, new Jp2FormatDescriptor());
    Assert.That(meta, Is.Not.Null, "JP2 reader produced no metadata.ini");
    Assert.That(meta!, Does.Contain("width=4"), $"JP2 width mismatch:\n{meta}");
    Assert.That(meta!, Does.Contain("height=4"), $"JP2 height mismatch:\n{meta}");
  }

  [Test]
  public void Avif_ImageMagickReference_ParsedByOurReader() {
    RequireTool("convert");
    if (!FsInteropToolbox.WslHasTool("avifdec"))
      Assert.Ignore("avifdec not installed. `sudo apt install -y libavif-bin`.");
    var avif = this.MakeReference("ref.avif", "-size 4x4 xc:red");

    // Confirm the reference really is a decodable AVIF (tool side reports 4x4).
    var dec = FsInteropToolbox.RunWsl($"avifdec --info {this.WslPathFor("ref.avif")}");
    Assert.That(dec.StdOut + dec.StdErr, Does.Contain("4x4"), $"avifdec did not see 4x4:\n{dec.StdOut}\n{dec.StdErr}");

    using var fs = File.OpenRead(avif);
    var desc = new AvifFormatDescriptor();
    var entries = desc.List(fs, null);
    Assert.That(entries, Is.Not.Empty, "AVIF reader returned no entries");
    Assert.That(entries.Any(e => e.Name.Contains("av1", StringComparison.OrdinalIgnoreCase)
                                 || e.Name.StartsWith("primary", StringComparison.OrdinalIgnoreCase)),
      Is.True, $"AVIF reader found no coded image item:\n{string.Join("\n", entries.Select(e => e.Name))}");
  }

  // ── Direction 1: our output → tool validates ────────────────────────

  [Test]
  public void Ico_OurWriter_ValidatedByIdentify() {
    RequireTool("identify");
    // Seed a real PNG via ImageMagick, then build an ICO around it with our
    // native writer. identify must recognise the container as ICO.
    var seedPng = this.MakeReference("seed.png", "-size 4x4 xc:red");
    var icoBytes = IcoWriter.BuildIco([new IcoWriter.Image(File.ReadAllBytes(seedPng))]);
    var icoPath = Path.Combine(this._tmpDir, "out.ico");
    File.WriteAllBytes(icoPath, icoBytes);

    var r = FsInteropToolbox.RunWsl($"identify -verbose {this.WslPathFor("out.ico")}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"identify rejected our ICO:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("ICO").IgnoreCase, $"identify did not call it ICO:\n{r.StdOut}");
    // The single embedded image is 4x4.
    Assert.That(r.StdOut, Does.Match(@"Geometry:\s*4x4"), $"identify geometry mismatch:\n{r.StdOut}");
  }

  [Test]
  public void Ico_OurWriter_ValidatedByExiftool() {
    RequireTool("exiftool");
    var seedPng = this.MakeReference("seed.png", "-size 4x4 xc:lime");
    var icoBytes = IcoWriter.BuildIco([new IcoWriter.Image(File.ReadAllBytes(seedPng))]);
    var icoPath = Path.Combine(this._tmpDir, "out.ico");
    File.WriteAllBytes(icoPath, icoBytes);

    var r = FsInteropToolbox.RunWsl($"exiftool {this.WslPathFor("out.ico")}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"exiftool rejected our ICO:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("Image Width").And.Contain("4"), $"exiftool width missing:\n{r.StdOut}");
    Assert.That(r.StdOut, Does.Contain("Image Height").And.Contain("4"), $"exiftool height missing:\n{r.StdOut}");
  }

  [Test]
  public void Webp_OurReader_RoundTripsToolDimensions_ViaDwebp() {
    RequireTool("dwebp");
    if (!FsInteropToolbox.WslHasTool("cwebp"))
      Assert.Ignore("cwebp not installed. `sudo apt install -y webp`.");
    this.MakeReference("seed.png", "-size 4x4 xc:\"rgba(0,0,255,0.5)\"");
    var enc = FsInteropToolbox.RunWsl($"cwebp -quiet {this.WslPathFor("seed.png")} -o {this.WslPathFor("rt.webp")}");
    var webp = Path.Combine(this._tmpDir, "rt.webp");
    Assert.That(File.Exists(webp), Is.True, $"cwebp failed:\n{enc.StdErr}");

    // Tool side: dwebp must agree the dimensions are 4x4.
    var dec = FsInteropToolbox.RunWsl($"dwebp {this.WslPathFor("rt.webp")} -o /dev/null");
    Assert.That(dec.StdOut + dec.StdErr, Does.Contain("4 x 4"), $"dwebp dims mismatch:\n{dec.StdOut}\n{dec.StdErr}");

    // Reader side: our descriptor must report the same canvas dimensions.
    var meta = ExtractMetadataIni("Webp", webp, new WebpFormatDescriptor());
    Assert.That(meta, Is.Not.Null);
    Assert.That(meta!, Does.Contain("width=4").And.Contain("height=4"), $"our reader dims mismatch:\n{meta}");
  }

  // ── Shared assertion helpers ────────────────────────────────────────

  /// <summary>
  /// Runs a descriptor's <c>Extract</c> and returns the text of the emitted
  /// <c>metadata.ini</c> (searched recursively), or null when absent.
  /// </summary>
  private string? ExtractMetadataIni(string label, string imagePath, IArchiveFormatOperations desc) {
    var outDir = Path.Combine(this._tmpDir, $"x_{label}");
    Directory.CreateDirectory(outDir);
    using (var fs = File.OpenRead(imagePath))
      desc.Extract(fs, outDir, null, null);
    var ini = Directory.EnumerateFiles(outDir, "metadata.ini", SearchOption.AllDirectories).FirstOrDefault();
    return ini is null ? null : File.ReadAllText(ini);
  }
}
