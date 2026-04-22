using System.Text;
using FileFormat.Fits;

namespace Compression.Tests.Fits;

[TestFixture]
public class FitsTests {
  // ── Minimal synthetic FITS builder ───────────────────────────────────────

  private const int CardSize = 80;
  private const int BlockSize = 2880;

  private static string PadCard(string keyword, string valuePart) {
    // keyword (left-aligned, 8 chars) + "= " + value/comment; total 80
    var kw = keyword.Length >= 8 ? keyword[..8] : keyword.PadRight(8);
    var full = kw + "= " + valuePart;
    if (full.Length > CardSize)
      full = full[..CardSize];
    return full.PadRight(CardSize);
  }

  private static string EndCard() => "END".PadRight(CardSize);

  private static byte[] BuildMinimalFits() {
    // Build a primary HDU: SIMPLE=T, BITPIX=8, NAXIS=2, NAXIS1=4, NAXIS2=4, END
    var cards = new List<string> {
      // Per FITS spec, SIMPLE 'T' sits at column 30 (1-indexed). Our PadCard uses "= "
      // after an 8-char keyword, so we pad the value part to place T at col 30.
      PadCard("SIMPLE", "                   T"),
      PadCard("BITPIX", "                    8"),
      PadCard("NAXIS",  "                    2"),
      PadCard("NAXIS1", "                    4"),
      PadCard("NAXIS2", "                    4"),
      PadCard("OBJECT", "'M31     '           / target object"),
      PadCard("TELESCOP","'Synthetic'          / telescope"),
      EndCard(),
    };

    var headerText = string.Concat(cards);
    // Pad header to next 2880-byte boundary.
    var headerPad = (BlockSize - headerText.Length % BlockSize) % BlockSize;
    headerText += new string(' ', headerPad);
    var headerBytes = Encoding.ASCII.GetBytes(headerText);

    // 16 bytes of data (4x4 image, BITPIX=8).
    var data = new byte[16];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);

    // Pad data to next 2880-byte boundary.
    var dataBlock = new byte[BlockSize];
    Array.Copy(data, 0, dataBlock, 0, data.Length);

    var blob = new byte[headerBytes.Length + dataBlock.Length];
    Array.Copy(headerBytes, 0, blob, 0, headerBytes.Length);
    Array.Copy(dataBlock, 0, blob, headerBytes.Length, dataBlock.Length);
    return blob;
  }

  // ── Descriptor metadata ──────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_ReportsFitsExtensions() {
    var d = new FitsFormatDescriptor();
    Assert.That(d.DefaultExtension, Is.EqualTo(".fits"));
    Assert.That(d.Extensions, Does.Contain(".fits"));
    Assert.That(d.Extensions, Does.Contain(".fit"));
    Assert.That(d.Extensions, Does.Contain(".fts"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_MagicSignature_StartsWithSimple() {
    var d = new FitsFormatDescriptor();
    Assert.That(d.MagicSignatures, Is.Not.Empty);
    var magic = Encoding.ASCII.GetString(d.MagicSignatures[0].Bytes);
    Assert.That(magic, Does.StartWith("SIMPLE"));
  }

  // ── List / Extract on minimal synthetic primary HDU ──────────────────────

  [Category("HappyPath")]
  [Test]
  public void List_ReturnsFullHeaderDataAndMetadata() {
    var blob = BuildMinimalFits();
    using var ms = new MemoryStream(blob);
    var d = new FitsFormatDescriptor();
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.fits"));
    Assert.That(names, Does.Contain("hdu_00_primary.header"));
    Assert.That(names, Does.Contain("hdu_00_primary.data"));
    Assert.That(names, Does.Contain("metadata.ini"));
  }

  [Category("HappyPath")]
  [Test]
  public void List_PrimaryDataEntry_Is16Bytes() {
    var blob = BuildMinimalFits();
    using var ms = new MemoryStream(blob);
    var d = new FitsFormatDescriptor();
    var entries = d.List(ms, null);
    var dataEntry = entries.First(e => e.Name == "hdu_00_primary.data");
    Assert.That(dataEntry.OriginalSize, Is.EqualTo(16));
  }

  [Category("HappyPath")]
  [Test]
  public void Extract_WritesHeaderDataAndMetadata() {
    var blob = BuildMinimalFits();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(blob);
      var d = new FitsFormatDescriptor();
      d.Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.fits")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "hdu_00_primary.header")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "hdu_00_primary.data")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);

      var dataBytes = File.ReadAllBytes(Path.Combine(tmp, "hdu_00_primary.data"));
      Assert.That(dataBytes, Has.Length.EqualTo(16));
      Assert.That(dataBytes[0], Is.EqualTo(1));
      Assert.That(dataBytes[15], Is.EqualTo(16));

      var metaText = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(metaText, Does.Contain("hdu_count=1"));
      Assert.That(metaText, Does.Contain("bitpix=8"));
      Assert.That(metaText, Does.Contain("naxis=2"));
      Assert.That(metaText, Does.Contain("parse_status=ok"));
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  // ── Robustness ───────────────────────────────────────────────────────────

  [Category("Robustness")]
  [Test]
  public void List_CorruptedInput_ReturnsAtLeastFullAndMetadata() {
    var blob = new byte[200];
    Array.Fill(blob, (byte)0x42);
    using var ms = new MemoryStream(blob);
    var d = new FitsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.fits"));
    Assert.That(names, Does.Contain("metadata.ini"));
  }
}
