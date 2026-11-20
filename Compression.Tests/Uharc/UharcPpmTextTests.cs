using Compression.Registry;
using FileFormat.Uharc;

namespace Compression.Tests.Uharc;

/// <summary>
/// Text-stream PPM writing (docs/UHARC-ON-DISK.md, "PPM", Text transform, and "Writing"): the
/// symbols <see cref="UharcPpmSymbols"/> plans for leading text files, and the 0.6, 0.4 and 0.3
/// PPM encoders driving their models with them.
/// </summary>
[TestFixture]
public class UharcPpmTextTests {

  /// <summary>
  /// Text files that exercise the word transform — capitalised and upper-case words, every byte
  /// from 0x80, LF / CRLF / CRLF-space line ends, space runs, tabs, a form feed, an end-of-file
  /// byte, and words split across file ends — and, unless <paramref name="textOnly"/>, binary
  /// files behind them.
  /// </summary>
  internal static IReadOnlyList<(string Name, byte[] Data)> TextFiles(bool textOnly = false) {
    var random = new Random(2026);
    var words = "the and for that with this tion ing ent you ver her com ess all his ter Alice The Then NASA OK I a A mention".Split(' ');
    byte[] Paragraph(int count, string lineEnd) {
      var text = new List<byte>();
      for (var i = 0; i < count; ++i) {
        text.AddRange(System.Text.Encoding.ASCII.GetBytes(words[random.Next(words.Length)]));
        var r = random.NextDouble();
        if (r < 0.08)
          text.AddRange(System.Text.Encoding.ASCII.GetBytes(lineEnd));
        else if (r < 0.10)
          text.AddRange(System.Text.Encoding.ASCII.GetBytes(lineEnd + new string(' ', random.Next(1, 4))));
        else if (r < 0.13)
          text.AddRange(Enumerable.Repeat((byte)' ', random.Next(2, 41)));
        else if (r < 0.14)
          text.Add((byte)'\t');
        else if (r < 0.15)
          text.Add((byte)random.Next(0x80, 0x100));
        else
          text.Add((byte)' ');
      }

      return [.. text];
    }

    var noise = new byte[3000];
    random.NextBytes(noise);
    List<(string, byte[])> files = [
      ("crlf.txt", Paragraph(2500, "\r\n")),
      ("lf.txt", [.. Paragraph(1500, "\n"), 0x1A]),
      ("high.txt", [.. Enumerable.Range(0x80, 0x80).Select(b => (byte)b), .. Paragraph(2500, "\r\n")]),
      ("split1.txt", "a word that ends in Capital T"u8.ToArray()),
      ("split2.txt", "he rest of it\r"u8.ToArray()),
      ("split3.txt", "\nnext line  "u8.ToArray()),
      ("empty.txt", []),
      ("one.txt", "x"u8.ToArray()),
      ("page.txt", "\fform feed\r\n\r\n  indented\r\n other"u8.ToArray()),
    ];
    if (!textOnly)
      files.AddRange([("noise.bin", noise), ("table.dat", [.. Paragraph(300, "\r\n"), .. Enumerable.Range(0, 128).Select(b => (byte)(b & 31))])]);
    return files;
  }

  private static (byte[] Data, List<long> Ends) Stream(IReadOnlyList<(string Name, byte[] Data)> files) {
    var ends = new List<long>();
    long end = 0;
    foreach (var (_, data) in files)
      ends.Add(end += data.Length);
    return ([.. files.SelectMany(f => f.Data)], ends);
  }

  /// <summary>Expands the planned symbols as the decoder's transform does.</summary>
  private static byte[] Expand(UharcPpmSymbols plan, int length) {
    var output = new byte[length];
    var transform = new UharcPpmTextTransform();
    var written = 0;
    for (var k = 0; k < plan.Symbols.Length; ++k) {
      Assert.That(plan.StartOf(k), Is.EqualTo(written), $"start of symbol {k}");
      written = transform.Emit(plan.Symbols[k], written, plan.TextLength, long.MaxValue, output);
    }

    Assert.That(written, Is.EqualTo(length));
    return output;
  }

  [Test, Category("HappyPath")]
  public void GivenTextThenBinaryFiles_WhenPlanned_ThenTheSymbolsExpandToTheStreamAndTheTextEndsAtTheLastTextFile() {
    var files = TextFiles();
    var (data, ends) = Stream(files);
    var plan = UharcPpmSymbols.Plan(data, ends);
    Assert.Multiple(() => {
      Assert.That(plan.TextLength, Is.EqualTo(ends[^3]));
      Assert.That(plan.Symbols.Length, Is.LessThan(data.Length), "strings save symbols");
      Assert.That(Expand(plan, data.Length), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenText_WhenPlanned_ThenCapitalisedWordsUseThePrefixAndRunningLineEndsAreStrings() {
    var data = "The cat\r\nsat.\r\nOK\r\n  x\nyes"u8.ToArray();
    var plan = UharcPpmSymbols.Plan(data, [data.Length]);
    var values = plan.Symbols.Select(s => (byte)~s).ToArray();
    // FD 9D "The"; " c" A1 "cat"; FA "\r\n" before a lower-case word; F8 "sa" "t."; 0D 0A before
    // "OK"; "OK"; 0D 0A 91 "  " before "x"; F9 "\n" before "yes"; "y" A0 "es".
    Assert.That(values, Is.EqualTo(new byte[] {
      0xFD, 0x9D, (byte)' ', (byte)'c', 0xA1, 0xFA, 0xF8, (byte)'t', (byte)'.', 0x0D, 0x0A, (byte)'O', (byte)'K', 0x0D, 0x0A, 0x91, (byte)'x',
      0xF9, (byte)'y', 0xA0,
    }));
    Assert.That(Expand(plan, data.Length), Is.EqualTo(data));
  }

  [Test, Category("Boundary")]
  public void GivenEveryByteFrom0x80_WhenPlanned_ThenReservedOnesGoThroughAnEscapePair() {
    var data = Enumerable.Range(0x80, 0x80).Select(b => (byte)b).Concat(new byte[0x780].Select(_ => (byte)'z')).ToArray();
    var plan = UharcPpmSymbols.Plan(data, [data.Length]);
    var reserved = Enumerable.Range(0x80, 0x80).Count(b => UharcPpmTables.TransformClass[b] < 200);
    Assert.Multiple(() => {
      Assert.That(reserved, Is.EqualTo(110));
      Assert.That(plan.TextSymbols, Is.EqualTo(0x80 + reserved + 0x780));
      Assert.That(Expand(plan, data.Length), Is.EqualTo(data));
    });
  }

  [Test, Category("Boundary")]
  public void GivenAStringAcrossAFileEnd_WhenPlanned_ThenNoSymbolSpansTheEnd() {
    // "  " and "the" would each be one string were the three files one.
    byte[] data = [.. "a "u8, .. " x th"u8, .. "e end"u8];
    long[] ends = [2, 7, data.Length];
    var plan = UharcPpmSymbols.Plan(data, ends);
    foreach (var end in ends)
      Assert.That(plan.StartOf(plan.IndexAt(end)), Is.EqualTo(end), $"a symbol starts at {end}");
    Assert.That(Expand(plan, data.Length), Is.EqualTo(data));
  }

  [TestCase(new byte[] { 0x41, 0x00 }, false)]
  [TestCase(new byte[] { 0x41, 0x1B }, false)]
  [TestCase(new byte[] { 0x41, 0x09, 0x0A, 0x0C, 0x0D, 0x1A }, true)]
  [TestCase(new byte[0], true)]
  [Category("Boundary")]
  public void GivenAFile_WhenCheckedForText_ThenOnlyTextControlBytesPass(byte[] file, bool text)
    => Assert.That(UharcPpmSymbols.IsText(file), Is.EqualTo(text));

  [TestCase(16, true)]
  [TestCase(17, false)]
  [Category("Boundary")]
  public void GivenHighBytes_WhenCheckedForText_ThenAtMostOneInSixteenPasses(int high, bool text) {
    var file = Enumerable.Repeat((byte)'a', 256 - high).Concat(Enumerable.Repeat((byte)0xE9, high)).ToArray();
    Assert.That(UharcPpmSymbols.IsText(file), Is.EqualTo(text));
  }

  [Test, Category("Boundary")]
  public void GivenBinaryFirst_WhenPlanned_ThenTheStreamIsBinary() {
    byte[] data = [0, 1, 2, .. "text"u8];
    var plan = UharcPpmSymbols.Plan(data, [3, data.Length]);
    Assert.Multiple(() => {
      Assert.That(plan.TextLength, Is.Zero);
      Assert.That(plan.Symbols, Is.EqualTo(data.Select(b => (byte)~b).ToArray()));
    });
  }

  [TestCase(1024)]
  [TestCase(0)]
  [Category("RoundTrip")]
  public void GivenTextFiles_WhenPpmEncodedPerVersion_ThenEachDecoderGivesThemBack(int window) {
    var (data, ends) = Stream(TextFiles());
    var size = window == 0 ? data.Length : window;
    var method06 = UharcWriter.LzpMethodFor(size) - UharcMethods.LzpFirst + UharcMethods.PpmFirst;
    var method03 = UharcMethods.Ppm03First + (method06 - UharcMethods.PpmFirst);
    var plan = UharcPpmSymbols.Plan(data, ends);
    Assert.Multiple(() => {
      var packed = UharcPpmEncoder.Encode(data, method06, ends, plan);
      Assert.That(UharcPpmDecoder.Decode(packed, 0, packed.Length, method06, data.Length, ends), Is.EqualTo(data), "0.6");
      packed = Uharc04PpmEncoder.Encode(data, method06, ends, plan);
      Assert.That(Uharc04PpmDecoder.Decode(packed, 0, packed.Length, method06, data.Length, ends), Is.EqualTo(data), "0.4");
      packed = Uharc03PpmEncoder.Encode(data, method03, ends, plan);
      Assert.That(Uharc03PpmDecoder.Decode(packed, 0, packed.Length, method03, data.Length, ends), Is.EqualTo(data), "0.3");
    });
  }

  [Test, Category("RoundTrip")]
  public void GivenOnlyText_WhenPpmEncodedAsText_ThenTheWholeStreamIsTextAndComesBack() {
    var (data, ends) = Stream(TextFiles(textOnly: true));
    var plan = UharcPpmSymbols.Plan(data, ends);
    Assert.That(plan.TextLength, Is.EqualTo(data.Length));
    var packed = UharcPpmEncoder.Encode(data, 32, ends, plan);
    Assert.That(UharcPpmDecoder.Decode(packed, 0, packed.Length, 32, data.Length, ends), Is.EqualTo(data));
  }

  /// <summary>Prose, and records of the kind a text stream codes worse than a binary one.</summary>
  private static IEnumerable<TestCaseData> TextKinds() {
    yield return new TestCaseData(Stream(TextFiles(textOnly: true)).Data).SetName("TextOrBinary_Prose");
    yield return new TestCaseData(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"record {i:D6} status {(i % 13 == 0 ? "fail" : "ok")}\n"))))
      .SetName("TextOrBinary_Records");
  }

  [TestCaseSource(nameof(TextKinds))]
  [Category("HappyPath")]
  public void GivenAllTextStream_WhenPpmEncoded_ThenTheSmallerOfTextAndBinaryIsKeptPerVersion(byte[] data) {
    long[] ends = [data.Length];
    var text = UharcPpmSymbols.Plan(data, ends);
    var binary = UharcPpmSymbols.Binary(data);
    Assert.Multiple(() => {
      var packed = UharcPpmEncoder.Encode(data, 32, ends);
      Assert.That(packed.Length, Is.EqualTo(Math.Min(UharcPpmEncoder.Encode(data, 32, ends, text).Length, UharcPpmEncoder.Encode(data, 32, ends, binary).Length)), "0.6");
      Assert.That(UharcPpmDecoder.Decode(packed, 0, packed.Length, 32, data.Length, ends), Is.EqualTo(data), "0.6");
      packed = Uharc04PpmEncoder.Encode(data, 32, ends);
      Assert.That(packed.Length, Is.EqualTo(Math.Min(Uharc04PpmEncoder.Encode(data, 32, ends, text).Length, Uharc04PpmEncoder.Encode(data, 32, ends, binary).Length)), "0.4");
      packed = Uharc03PpmEncoder.Encode(data, 30, ends);
      Assert.That(packed.Length, Is.EqualTo(Math.Min(Uharc03PpmEncoder.Encode(data, 30, ends, text).Length, Uharc03PpmEncoder.Encode(data, 30, ends, binary).Length)), "0.3");
    });
  }

  [TestCase(10, 20, true)]
  [TestCase(20, 20, true)]
  [TestCase(21, 20, false)]
  [Category("Boundary")]
  public void GivenTextBeforeBinary_WhenTheLeadingTextPacksSmallerOrEqualAsText_ThenTheTextStreamIsUsed(int textSize, int binarySize, bool text) {
    byte[] data = [.. "some text"u8, 0, 1, 2];
    long[] ends = [9, data.Length];
    var chosen = new List<long>();
    UharcPpmSymbols.EncodeSmaller(data, ends, (d, e, plan) => {
      if (d.Length < data.Length)
        return new byte[plan.TextLength > 0 ? textSize : binarySize];
      chosen.Add(plan.TextLength);
      return [];
    });
    Assert.That(chosen, Is.EqualTo(new[] { text ? 9L : 0L }));
  }

  [TestCase("6")]
  [TestCase("4")]
  [TestCase("3")]
  [Category("HappyPath")]
  public void GivenBinaryBeforeText_WhenWrittenAsPpm_ThenTextFilesComeFirstAndEveryFileComesBack(string version) {
    var files = TextFiles();
    IReadOnlyList<ArchiveInputInfo> inputs = [.. files.Reverse().Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data))];
    var reader = new UharcReader(new MemoryStream(UharcWriterTests.CreateWith(new FormatCreateOptions("ppm") {
      FormatSpecific = { [UharcFormatDescriptor.VersionOption] = version },
    }, inputs)));
    var order = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.FileName).ToList();
    Assert.Multiple(() => {
      Assert.That(order[^2..], Is.EquivalentTo(new[] { "noise.bin", "table.dat" }));
      foreach (var (name, data) in files)
        Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == name)), Is.EqualTo(data), name);
    });
  }
}
