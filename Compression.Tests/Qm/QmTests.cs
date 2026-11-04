using System.Diagnostics;
using System.Reflection;
using System.Text;
using Compression.Registry;
using FileFormat.Qm;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Qm;

/// <summary>
/// Qt QM catalogs. Every <c>.qm</c> fixture was compiled by Qt's own <c>lrelease</c> 6.12.0 from the
/// <c>.ts</c> beside it (see ReferenceVectors/README.md): our compiler must reproduce each byte for byte
/// and our decompiler must produce TS that compiles back to the same bytes. Where available, the live
/// <c>lrelease</c> and Qt's runtime <c>QTranslator</c> (through PySide6) are asked as well.
/// </summary>
[TestFixture]
public class QmTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"QmVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded QM vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly string[] Pairs = ["sample_de", "sample_pt", "sample_pt_PT", "sample_ar", "sample_ja", "sample_none", "edge_fr", "empty_en"];

  private static byte[] Compile(byte[] ts) {
    using var ms = new MemoryStream();
    new QmFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("x.ts", ts)], new FormatCreateOptions());
    return ms.ToArray();
  }

  private static byte[] Decompile(byte[] qm) => new QmFormatDescriptor().ExtractToMemory(qm);

  [TestCaseSource(nameof(Pairs))]
  [Category("ThirdPartyVector")]
  public void Given_TsFile_When_Compiled_Then_ByteIdenticalToLrelease(string name)
    => Assert.That(Compile(Vector(name + ".ts")), Is.EqualTo(Vector(name + ".qm")));

  [TestCaseSource(nameof(Pairs))]
  [Category("ThirdPartyVector")]
  public void Given_LreleaseOutput_When_Decompiled_Then_CompilesBackToTheSameBytes(string name) {
    var qm = Vector(name + ".qm");
    Assert.That(Compile(Decompile(qm)), Is.EqualTo(qm));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_GermanCatalog_When_Read_Then_MessagesLanguageAndRulesDecode() {
    var qm = QmCatalog.Read(Vector("sample_de.qm"));
    Assert.That(qm.Language, Is.EqualTo("de_DE"));
    Assert.That(qm.NumerusRules, Is.EqualTo(new byte[] { 0x01, 0x01 }));
    Assert.That(qm.Messages, Has.Count.EqualTo(7));
    var plural = qm.Messages.Single(m => m.SourceText == "%n file(s) selected");
    Assert.That(plural.Translations, Is.EqualTo(new[] { "%n Datei ausgewählt", "%n Dateien ausgewählt" }));
    // lrelease dropped the comment of the first "Open" (QTranslator retries without one) and kept the second.
    Assert.That(qm.Messages.Where(m => m.SourceText == "Open").Select(m => m.Comment), Is.EquivalentTo(new[] { "", "adjective" }));
    Assert.That(qm.Messages.Single(m => m.Context == "Dialog" && m.SourceText!.StartsWith("Gr")).Translations[0], Does.EndWith("✓ 😀"));
    Assert.That(qm.Messages.Any(m => m.SourceText == "Gone"), Is.False, "vanished messages are not compiled");
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_EmptyTranslations_When_Read_Then_NullStringsAndByteEscapesSurvive() {
    var qm = QmCatalog.Read(Vector("empty_en.qm"));
    Assert.That(qm.Messages.Single(m => m.SourceText == "Empty finished").Translations, Is.EqualTo(new string?[] { null }));
    Assert.That(qm.Messages.Single(m => m.SourceText == "Byte").Translations[0], Is.EqualTo("a\u0007b"));
    Assert.That(Encoding.UTF8.GetString(Decompile(Vector("empty_en.qm"))), Does.Contain("a<byte value=\"x7\"/>b"));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_FrenchEdgeCases_When_Read_Then_DependenciesVariantsAndKeptCommentsDecode() {
    var qm = QmCatalog.Read(Vector("edge_fr.qm"));
    Assert.That(qm.Dependencies, Is.EqualTo(new[] { "qtbase_fr", "qtmultimedia_fr" }));
    Assert.That(qm.Messages.Single(m => m.SourceText == "No context").Comment, Is.EqualTo("kept because the context is empty"));
    Assert.That(qm.Messages.Single(m => m.SourceText == "Copy" && m.Comment == "noun").Translations[0], Is.EqualTo("Copie"));
    Assert.That(qm.Messages.Single(m => m.SourceText == "Save as...").Translations[0], Is.EqualTo("Enregistrer sous...\u009CEnreg. sous"));
    Assert.That(qm.Messages.Single(m => m.SourceText == "Paste").Translations[0], Is.EqualTo("Coller"));
    Assert.That(qm.Messages.Any(m => m.SourceText is "Undo" or "Cut"), Is.False);
    Assert.That(qm.Messages.Single(m => m.SourceText == "%n item(s)").Translations, Is.EqualTo(new string?[] { "%n élément", null }));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_CompressedLreleaseOutput_When_Listed_Then_ReadWithStrippedSourcesMarked() {
    var data = Vector("sample_de_compress.qm");
    var qm = QmCatalog.Read(data);
    Assert.That(qm.HasContextTable, Is.True);
    Assert.That(qm.Messages, Has.Count.EqualTo(7));
    Assert.That(qm.Messages.All(m => m.Context != null), Is.True);
    var list = new QmFormatDescriptor().List(new MemoryStream(data), null);
    Assert.That(list.Single().Name, Is.EqualTo("translations.ts"));
    var ts = Encoding.UTF8.GetString(Decompile(data));
    Assert.That(ts, Does.Contain("Abbrechen"));
    Assert.That(qm.IsStripped, Is.EqualTo(ts.Contains("source text not stored")));
  }

  [TestCase("de", 2, new byte[] { 1, 1 })]
  [TestCase("de_AT", 2, new byte[] { 1, 1 })]
  [TestCase("pt", 2, new byte[] { 3, 1 })]
  [TestCase("pt_BR", 2, new byte[] { 3, 1 })]
  [TestCase("pt-PT", 2, new byte[] { 1, 1 })]
  [TestCase("ja", 1, new byte[0])]
  [TestCase("fil", 2, new byte[] { 3, 1 })]
  [TestCase("ar", 6, new byte[] { 1, 0, 0xFF, 1, 1, 0xFF, 1, 2, 0xFF, 0x24, 3, 10, 0xFF, 0x2A, 11 })]
  [Category("EdgeCase")]
  public void Given_LanguageCode_When_RulesLookedUp_Then_LreleaseAssignment(string code, int forms, byte[] rules) {
    var r = QmNumerus.For(code);
    Assert.That(r, Is.Not.Null);
    Assert.That(r!.Value.Forms, Is.EqualTo(forms));
    Assert.That(r.Value.Rules, Is.EqualTo(rules));
  }

  [TestCase(null), TestCase(""), TestCase("xx"), TestCase("C")]
  [Category("EdgeCase")]
  public void Given_UnknownLanguage_When_RulesLookedUp_Then_None(string? code)
    => Assert.That(QmNumerus.For(code), Is.Null);

  [Test, Category("EdgeCase")]
  public void Given_NoMessages_When_Compiled_Then_OnlyMagicAndLanguage() {
    var qm = Compile("<TS version=\"2.1\" language=\"ja\"></TS>"u8.ToArray());
    Assert.That(qm, Is.EqualTo(QmCatalog.Magic.ToArray().Concat(new byte[] { 0xA7, 0, 0, 0, 2, (byte)'j', (byte)'a' })));
    Assert.That(QmCatalog.Read(qm).Messages, Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Given_IdBasedTs_When_Compiled_Then_NotSupported()
    => Assert.Throws<NotSupportedException>(() => Compile("<TS><context><name>c</name><message id=\"a\"><source>s</source><translation>t</translation></message></context></TS>"u8.ToArray()));

  [Test, Category("EdgeCase")]
  public void Given_MalformedTs_When_Compiled_Then_InvalidData() {
    Assert.Throws<InvalidDataException>(() => Compile("<TS><context>"u8.ToArray()));
    Assert.Throws<InvalidDataException>(() => Compile("<html/>"u8.ToArray()));
  }

  [TestCase(0), TestCase(2)]
  [Category("EdgeCase")]
  public void Given_NotExactlyOneTs_When_Created_Then_Throws(int count)
    => Assert.Throws<ArgumentException>(() => new QmFormatDescriptor().Create(new MemoryStream(),
      [.. Enumerable.Range(0, count).Select(i => ArchiveInputInfo.InMemory($"{i}.ts", Vector("sample_de.ts")))], new FormatCreateOptions()));

  [Test, Category("EdgeCase")]
  public void Given_WrongMagic_When_Read_Then_InvalidData() {
    var data = Vector("sample_de.qm");
    data[0] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => QmCatalog.Read(data));
  }

  [Test, Category("EdgeCase")]
  public void Given_SectionLongerThanTheFile_When_Read_Then_InvalidData() {
    var data = Vector("sample_de.qm");
    data[17] = 0x7F; // Language section length high byte
    Assert.Throws<InvalidDataException>(() => QmCatalog.Read(data));
  }

  [Test, Category("EdgeCase")]
  public void Given_HashOffsetPastTheMessages_When_Read_Then_InvalidData() {
    var data = Vector("sample_de.qm");
    // First hash entry's offset (Hashes section header at 0x1A: tag, length, then hash, offset).
    data[0x1A + 5 + 4] = 0x7F;
    Assert.Throws<InvalidDataException>(() => QmCatalog.Read(data));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedCatalog_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => QmCatalog.Read(Vector("sample_de.qm")[..300]));

  [Test, Category("EdgeCase")]
  public void Given_ElfHash_When_Computed_Then_QtValues() {
    Assert.That(QmCatalog.ElfHash(""u8), Is.EqualTo(1u));
    Assert.That(QmCatalog.ElfHash("Cancel"u8), Is.EqualTo(ReadFirstHashFor("Cancel")));
    Assert.That(QmCatalog.ElfHash("%n file(s) selected"u8), Is.EqualTo(ReadFirstHashFor("%n file(s) selected")));
  }

  private static uint ReadFirstHashFor(string source) {
    // The hash lrelease stored for the record whose source is `source` in sample_de.qm.
    var data = Vector("sample_de.qm");
    var hashes = 0x1A + 5;
    var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x1A + 1)) / 8;
    var messages = hashes + (int)count * 8 + 5;
    for (var i = 0; i < count; ++i) {
      var hash = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(hashes + i * 8));
      var offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(hashes + i * 8 + 4));
      var record = Encoding.Latin1.GetString(data, messages + offset, Math.Min(120, data.Length - messages - offset));
      if (record.Contains("\u0006\0\0\0" + (char)source.Length + source)) return hash;
    }
    throw new InvalidOperationException("record not found");
  }

  // ── live Qt tools ────────────────────────────────────────────────────

  private static string? FindLrelease() {
    if (Environment.GetEnvironmentVariable("CWB_TOOL_LRELEASE") is { Length: > 0 } p && File.Exists(p)) return p;
    foreach (var name in new[] { "lrelease", "lrelease-qt6", "lrelease6" })
      foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        foreach (var file in new[] { name, name + ".exe" })
          if (dir.Length > 0 && File.Exists(Path.Combine(dir, file))) return Path.Combine(dir, file);
    return null;
  }

  [TestCaseSource(nameof(Pairs))]
  [Category("ArchiveExternal")]
  public void Given_DecompiledTs_When_CompiledByLrelease_Then_TheOriginalBytes(string name) {
    if (FindLrelease() is not { } lrelease) { Assert.Ignore("lrelease not found (CWB_TOOL_LRELEASE or PATH)."); return; }
    var tmp = Directory.CreateTempSubdirectory("cwb_qm_").FullName;
    try {
      var ts = Path.Combine(tmp, "in.ts");
      var qm = Path.Combine(tmp, "out.qm");
      File.WriteAllBytes(ts, Decompile(Vector(name + ".qm")));
      var (exit, _, stderr) = Run(lrelease, ["-silent", ts, "-qm", qm]);
      Assert.That(exit, Is.Zero, stderr);
      Assert.That(File.ReadAllBytes(qm), Is.EqualTo(Vector(name + ".qm")));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  private const string TranslatorScript = """
    import os, sys
    sys.path[:0] = [p for p in os.environ.get('CWB_PYTHONPATH', '').split(os.pathsep) if p]
    from PySide6.QtCore import QCoreApplication, QTranslator
    app = QCoreApplication([])
    t = QTranslator()
    if not t.load(sys.argv[1]):
        sys.exit("load failed")
    print(t.language())
    for ctx, src, dis, n in [("MainWindow", "&File", None, -1), ("MainWindow", "Open", "verb", -1), ("MainWindow", "Open", "adjective", -1),
                             ("MainWindow", "Open", None, -1), ("MainWindow", "%n file(s) selected", None, 1),
                             ("MainWindow", "%n file(s) selected", None, 5), ("Dialog", "Grüße — “quoted” ✓", None, -1),
                             ("MainWindow", "Not done yet", None, -1), ("MainWindow", "Gone", None, -1)]:
        print(repr(t.translate(ctx, src, dis, n)))
    """;

  [Test, Category("ArchiveExternal")]
  public void Given_CreatedCatalog_When_LoadedByQTranslator_Then_LookupsAndPluralsResolve() {
    string? python = null;
    foreach (var candidate in new[] { "python3", "python" })
      try {
        if (Run(candidate, ["-c", "import os, sys; sys.path[:0] = [p for p in os.environ.get('CWB_PYTHONPATH', '').split(os.pathsep) if p]; import PySide6.QtCore"]).ExitCode == 0) { python = candidate; break; }
      } catch (Win32Exception) {
        // not on the PATH
      }
    if (python == null) { Assert.Ignore("No Python with PySide6 importable (pip install PySide6-Essentials, or point CWB_PYTHONPATH at it)."); return; }
    var tmp = Directory.CreateTempSubdirectory("cwb_qm_").FullName;
    try {
      var qm = Path.Combine(tmp, "ours.qm");
      File.WriteAllBytes(qm, Compile(Vector("sample_de.ts")));
      var script = Path.Combine(tmp, "tr.py");
      File.WriteAllText(script, TranslatorScript);
      var (exit, stdout, stderr) = Run(python, [script, qm]);
      Assert.That(exit, Is.Zero, stderr);
      Assert.That(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Is.EqualTo(new[] {
        "de_DE", "'&Datei'", "'Öffnen'", "'Offen'", "'Öffnen'", "'%n Datei ausgewählt'", "'%n Dateien ausgewählt'",
        "'Grüße — „zitiert“ ✓ 😀'", "'Noch nicht fertig'", "''",
      }));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  private static (int ExitCode, string StdOut, string StdErr) Run(string file, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(file) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8,
    };
    start.Environment["PYTHONUTF8"] = "1";
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }
}

internal static class QmDescriptorTestExtensions {
  public static byte[] ExtractToMemory(this QmFormatDescriptor d, byte[] qm) {
    var dir = Directory.CreateTempSubdirectory("cwb_qmx_").FullName;
    try {
      d.Extract(new MemoryStream(qm), dir, null, null);
      return File.ReadAllBytes(Path.Combine(dir, QmFormatDescriptor.EntryName));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
