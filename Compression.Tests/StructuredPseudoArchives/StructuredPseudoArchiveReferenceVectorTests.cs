using System.Text;
using Compression.Registry;
using FileFormat.Creg;
using FileFormat.Json;
using FileFormat.MessagePack;
using FileFormat.Nrbf;
using FileFormat.Pickle;
using FileFormat.Reg;
using FileFormat.Regf;
using FileFormat.Storable;
using FileFormat.Xml;

namespace Compression.Tests.StructuredPseudoArchives;

/// <summary>
/// Cross-implementation parity for every structured pseudo-archive format, in both directions:
/// what a reference implementation writes, we must read; what we write, a reference implementation
/// must be able to read — pinned as the very bytes that implementation produces for the same value.
///
/// This fixture carries NO NUnit category on purpose. Categories are what `ci.yml` filters out of
/// the `Core tests` step, and that step is the only one that gates a pull request. The predecessor
/// of the CREG assertion below lived in <c>ExternalInterop</c>, failed against the only real hive
/// it was ever pointed at, and merged anyway, because the gate never ran it.
/// </summary>
[TestFixture]
public sealed class StructuredPseudoArchiveReferenceVectorTests {
  // ---------------------------------------------------------------------------------- MessagePack

  [Test]
  public void MessagePack_CreateMatchesPythonMsgpackBytes() {
    var produced = ReferenceVectorFixture.Create(new MessagePackFormatDescriptor());
    var reference = ReferenceVectorFixture.Load("messagepack-archive.msgpack");
    Assert.That(ReferenceVectorFixture.Hex(produced), Is.EqualTo(ReferenceVectorFixture.Hex(reference)),
      "our MessagePack encoder no longer agrees byte for byte with python-msgpack's packb");
  }

  [Test]
  public void MessagePack_ReadsEveryTypeFamilyPythonMsgpackEmits() {
    var descriptor = new MessagePackFormatDescriptor();
    var bytes = ReferenceVectorFixture.Load("messagepack-types.msgpack");
    var entries = ReferenceVectorFixture.List(descriptor, bytes);

    string Kind(string name) => entries.Single(e => e.Name == name).Kind!;
    string Text(string name) => ReferenceVectorFixture.ExtractText(descriptor, bytes, name);

    Assert.Multiple(() => {
      Assert.That(Kind("nil"), Is.EqualTo("nil"));
      Assert.That(Text("nil"), Is.EqualTo("null"));
      Assert.That(Kind("true"), Is.EqualTo("bool"));
      Assert.That(Text("true"), Is.EqualTo("true"));
      Assert.That(Text("false"), Is.EqualTo("false"));

      // The integer width ladder. Each value is the smallest one python-msgpack encodes with the
      // format the entry is named after, so a reader that confuses two widths cannot pass all of them.
      Assert.That((Kind("positive-fixint-min"), Text("positive-fixint-min")), Is.EqualTo(("positive-fixint", "0")));
      Assert.That((Kind("positive-fixint-max"), Text("positive-fixint-max")), Is.EqualTo(("positive-fixint", "127")));
      Assert.That((Kind("negative-fixint-max"), Text("negative-fixint-max")), Is.EqualTo(("negative-fixint", "-1")));
      Assert.That((Kind("negative-fixint-min"), Text("negative-fixint-min")), Is.EqualTo(("negative-fixint", "-32")));
      Assert.That((Kind("uint8"), Text("uint8")), Is.EqualTo(("uint8", "255")));
      Assert.That((Kind("uint16"), Text("uint16")), Is.EqualTo(("uint16", "65535")));
      Assert.That((Kind("uint32"), Text("uint32")), Is.EqualTo(("uint32", "4294967295")));
      Assert.That((Kind("uint64"), Text("uint64")), Is.EqualTo(("uint64", "18446744073709551615")));
      Assert.That((Kind("int8"), Text("int8")), Is.EqualTo(("int8", "-128")));
      Assert.That((Kind("int16"), Text("int16")), Is.EqualTo(("int16", "-32768")));
      Assert.That((Kind("int32"), Text("int32")), Is.EqualTo(("int32", "-2147483648")));
      Assert.That((Kind("int64"), Text("int64")), Is.EqualTo(("int64", "-9223372036854775808")));
      Assert.That((Kind("float64"), Text("float64")), Is.EqualTo(("float64", "1.5")));

      // The 2.0 spec split: str carries UTF-8 text, bin carries opaque bytes, and a reader that
      // treats one as the other still round-trips through itself while failing on real input.
      Assert.That((Kind("fixstr"), Text("fixstr")), Is.EqualTo(("fixstr", "abc")));
      Assert.That((Kind("str8"), Text("str8")), Is.EqualTo(("str8", new string('s', 40))));
      Assert.That((Kind("str16"), Text("str16")), Is.EqualTo(("str16", new string('m', 300))));
      Assert.That(Text("utf8"), Is.EqualTo("\u00e4\u4e2d\U0001f600"));
      Assert.That(Kind("bin8"), Is.EqualTo("bin8"));
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, "bin8"), Is.EqualTo(new byte[] { 0x00, 0xff, 0x10 }));
      Assert.That(Kind("bin16"), Is.EqualTo("bin16"));
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, "bin16"), Has.Length.EqualTo(300));

      Assert.That(Text("fixarray/[000002]"), Is.EqualTo("3"));
      Assert.That(entries.Count(e => e.Name.StartsWith("array16/[", StringComparison.Ordinal)), Is.EqualTo(20));
      Assert.That(Text("fixmap/inner"), Is.EqualTo("1"));
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, "nested/list/[000000]/leaf"), Is.EqualTo(new byte[] { 0x01, 0x02 }));

      // Extension families, including the one negative type code the specification assigns.
      Assert.That(Kind("fixext1"), Is.EqualTo("fixext1:type=5"));
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, "fixext1"), Is.EqualTo(new byte[] { 0x2a }));
      Assert.That(Kind("fixext4"), Is.EqualTo("fixext4:type=3"));
      Assert.That(Kind("timestamp"), Is.EqualTo("fixext8:type=-1"));
      Assert.That(Kind("ext8"), Is.EqualTo("ext8:type=7"));

      // A non-string map key cannot be a path segment, so it projects as an explicit pair.
      Assert.That(Text("entry-000030/%24key"), Is.EqualTo("1"));
      Assert.That(Text("entry-000030/%24value"), Is.EqualTo("integer-key"));
    });
  }

  [Test]
  public void MessagePack_ReadsTheSingleFloatPythonMsgpackOnlyEmitsOnDemand() {
    var descriptor = new MessagePackFormatDescriptor();
    var bytes = ReferenceVectorFixture.Load("messagepack-float32.msgpack");
    Assert.Multiple(() => {
      Assert.That(ReferenceVectorFixture.List(descriptor, bytes).Single().Kind, Is.EqualTo("float32"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "float32"), Is.EqualTo("1.5"));
    });
  }

  // --------------------------------------------------------------------------------------- pickle

  [Test]
  public void Pickle_CreateMatchesCPythonProtocol4Bytes() {
    var produced = ReferenceVectorFixture.Create(new PickleFormatDescriptor());
    var reference = ReferenceVectorFixture.Load("pickle-protocol4-archive.pickle");
    Assert.That(ReferenceVectorFixture.Hex(produced), Is.EqualTo(ReferenceVectorFixture.Hex(reference)),
      "our pickle encoder no longer agrees byte for byte with CPython's pickle.dumps(obj, protocol=4)");
  }

  [Test]
  public void Pickle_ReadsTheArchiveCPythonItselfPickled() {
    var descriptor = new PickleFormatDescriptor();
    var bytes = ReferenceVectorFixture.Load("pickle-protocol4-archive.pickle");
    Assert.Multiple(() => {
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, "dir/file.bin"), Is.EqualTo(ReferenceVectorFixture.FileBin));
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, "dir/long.bin"), Is.EqualTo(ReferenceVectorFixture.LongBin));
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, "top.bin"), Is.EqualTo(ReferenceVectorFixture.TopBin));
    });
  }

  /// <summary>
  /// Protocol 0 is line-oriented ASCII and structurally unlike 2+; 4 adds framing and implicit
  /// memoisation; 5 adds out-of-band buffers. The reader accepts all six, so all six are pinned.
  /// </summary>
  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(5)]
  public void Pickle_ReadsCPythonOutputOnEveryProtocolItAccepts(int protocol) {
    var descriptor = new PickleFormatDescriptor();
    var bytes = ReferenceVectorFixture.Load($"pickle-protocol{protocol}-document.pickle");
    Assert.Multiple(() => {
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "dir/count"), Is.EqualTo("7"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "dir/name"), Is.EqualTo("value"));
    });
  }

  // ------------------------------------------------------------------------------------------ reg

  [Test]
  public void Reg_CreateMatchesWindowsRegExportBytes() {
    var produced = ReferenceVectorFixture.Create(new RegFormatDescriptor());
    var reference = ReferenceVectorFixture.Load("registry-export.reg");
    Assert.That(ReferenceVectorFixture.Hex(produced), Is.EqualTo(ReferenceVectorFixture.Hex(reference)),
      "our .reg writer no longer agrees byte for byte with `reg.exe export` -- check the UTF-16LE BOM, "
      + "the version banner, CRLF line endings on every platform, and the 80-column hex continuation");
  }

  [Test]
  public void Reg_ReadsEveryValueTypeWindowsRegExportEmits() {
    const string root = "HKEY_CURRENT_USER/Software/CompressionWorkbench/ReferenceTypes";
    var descriptor = new RegFormatDescriptor();
    var bytes = ReferenceVectorFixture.Load("registry-types.reg");
    var entries = ReferenceVectorFixture.List(descriptor, bytes);

    string Kind(string name) => entries.Single(e => e.Name == $"{root}/{name}").Kind!;
    string Text(string name) => ReferenceVectorFixture.ExtractText(descriptor, bytes, $"{root}/{name}");

    Assert.Multiple(() => {
      Assert.That((Kind("Sz"), Text("Sz")), Is.EqualTo(("REG_SZ", "plain text")));
      Assert.That((Kind("SzEscapes"), Text("SzEscapes")), Is.EqualTo(("REG_SZ", "quote \" and backslash \\ inside")));
      Assert.That((Kind("ExpandSz"), Text("ExpandSz")), Is.EqualTo(("REG_EXPAND_SZ", "%SystemRoot%\\system32")));
      // hex(7) is a run of NUL-terminated UTF-16LE strings closed by a second NUL.
      Assert.That((Kind("MultiSz"), Text("MultiSz")), Is.EqualTo(("REG_MULTI_SZ", "alpha\nbeta\ngamma")));
      Assert.That((Kind("Dword"), Text("Dword")), Is.EqualTo(("REG_DWORD", "0x0000002A")));
      Assert.That((Kind("DwordHigh"), Text("DwordHigh")), Is.EqualTo(("REG_DWORD", "0xFFFFFFFF")));
      Assert.That((Kind("Qword"), Text("Qword")), Is.EqualTo(("REG_QWORD", "0x1122334455667788")));
      Assert.That(Kind("Binary"), Is.EqualTo("REG_BINARY"));
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, $"{root}/Binary"), Is.EqualTo(new byte[] { 0x00, 0xff, 0x10 }));

      // 200 bytes, which `reg export` wraps over nine continuation lines. Reassembling them is the
      // part of the grammar a hand-written parser is most likely to truncate or mis-join.
      var wrapped = ReferenceVectorFixture.Extract(descriptor, bytes, $"{root}/BinaryWrapped");
      Assert.That(wrapped, Is.EqualTo(Enumerable.Range(0, 200).Select(x => (byte)(x * 7)).ToArray()));

      Assert.That(Text("sub/Nested"), Is.EqualTo("child"));
    });
  }

  // ----------------------------------------------------------------------------------- JSON / XML

  [Test]
  public void Json_CreateMatchesCPythonJsonDumpsBytes() {
    var produced = ReferenceVectorFixture.Create(new JsonFormatDescriptor());
    var reference = ReferenceVectorFixture.Load("json-archive.json");
    Assert.That(Encoding.UTF8.GetString(produced), Is.EqualTo(Encoding.UTF8.GetString(reference)),
      "our JSON writer no longer agrees byte for byte with CPython's json.dumps(obj, indent=2)");
  }

  [Test]
  public void Json_ReadsTheDocumentCPythonSerialised() {
    var descriptor = new JsonFormatDescriptor();
    var bytes = ReferenceVectorFixture.Load("json-document.json");
    Assert.Multiple(() => {
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "dir/count"), Is.EqualTo("7"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "dir/name"), Is.EqualTo("value"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "dir/ratio"), Is.EqualTo("1.5"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "dir/flag"), Is.EqualTo("true"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "dir/missing"), Is.EqualTo("null"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "dir/list/[000001]"), Is.EqualTo("two"));
    });
  }

  /// <summary>
  /// XML has no writer parity vector. Our creation path emits a namespaced <c>cwb:archive</c>
  /// envelope through <see cref="System.Xml.XmlWriter"/>, and no third-party XML writer reproduces
  /// another one's declaration quoting, attribute order and namespace placement byte for byte, so
  /// there is nothing honest to compare against. What is pinned instead is the reading direction,
  /// against a document CPython's ElementTree wrote; the writing direction is exercised live
  /// against a real parser by <c>StructuredPseudoArchiveExternalToolTests</c>.
  /// </summary>
  [Test]
  public void Xml_ReadsTheDocumentPythonElementTreeSerialised() {
    var descriptor = new XmlFormatDescriptor();
    var bytes = ReferenceVectorFixture.Load("xml-document.xml");
    Assert.Multiple(() => {
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "root/%40id"), Is.EqualTo("7"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "root/item~000000"), Is.EqualTo("A"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "root/item~000001"), Is.EqualTo("B"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, bytes, "root/nested/leaf"), Is.EqualTo("deep"));
    });
  }

  /// <summary>
  /// Our own archive envelope, rendered by ElementTree rather than by
  /// <see cref="System.Xml.XmlWriter"/>, and carrying the namespace under the prefix <c>arc</c>
  /// instead of our writer's <c>cwb</c>. An XML name is its namespace plus its local name; the
  /// prefix is only a spelling. A reader that quietly grew to match on the prefix reads its sibling
  /// writer's output perfectly and nothing else, and this is the vector that tells the two apart.
  /// </summary>
  [Test]
  public void Xml_ReadsItsOwnArchiveEnvelopeWhenAThirdPartyRenderedIt() {
    var descriptor = new XmlFormatDescriptor();
    var bytes = ReferenceVectorFixture.Load("xml-archive-elementtree.xml");
    Assert.Multiple(() => {
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, "dir/file.bin"), Is.EqualTo(ReferenceVectorFixture.FileBin));
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, "dir/long.bin"), Is.EqualTo(ReferenceVectorFixture.LongBin));
      Assert.That(ReferenceVectorFixture.Extract(descriptor, bytes, "top.bin"), Is.EqualTo(ReferenceVectorFixture.TopBin));
    });
  }

  // -------------------------------------------------------------------------------------- Storable

  /// <summary>
  /// Perl randomises hash iteration order per process, so only a tree of single-key hashes
  /// serialises to a stable byte sequence. That is what this vector holds, and it is the only
  /// input shape for which <c>nstore</c> and our writer are byte-comparable at all.
  /// </summary>
  [Test]
  public void Storable_CreateMatchesPerlNstoreBytes() {
    using var output = new MemoryStream();
    new StorableFormatDescriptor().Create(
      output,
      [ArchiveInputInfo.InMemory("dir/file.bin", ReferenceVectorFixture.FileBin)],
      new FormatCreateOptions());

    var reference = ReferenceVectorFixture.Load("storable-nested.storable");
    Assert.That(ReferenceVectorFixture.Hex(output.ToArray()), Is.EqualTo(ReferenceVectorFixture.Hex(reference)),
      "our Storable writer no longer agrees byte for byte with Perl's nstore");
  }

  [Test]
  public void Storable_ReadsWhatPerlNstoreWrote() {
    var descriptor = new StorableFormatDescriptor();
    var nested = ReferenceVectorFixture.Load("storable-nested.storable");
    Assert.That(ReferenceVectorFixture.Extract(descriptor, nested, "dir/file.bin"), Is.EqualTo(ReferenceVectorFixture.FileBin));

    var document = ReferenceVectorFixture.Load("storable-document.storable");
    Assert.Multiple(() => {
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, document, "dir/name"), Is.EqualTo("value"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, document, "dir/count"), Is.EqualTo("7"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, document, "dir/deep/leaf"), Is.EqualTo("bottom"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, document, "dir/list/[000000]"), Is.EqualTo("1"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, document, "dir/list/[000001]"), Is.EqualTo("two"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, document, "dir/list/[000002]"), Is.EqualTo("3"));
    });
  }

  // ------------------------------------------------------------------------------------------ NRBF

  /// <summary>
  /// MS-NRBF is read-only here, so only the reading direction exists. The vectors come from the
  /// producer that defined the format — .NET Framework's <c>BinaryFormatter</c>, still present in
  /// Windows PowerShell 5.1 — and are walked as records; no type is ever activated.
  /// </summary>
  [Test]
  public void Nrbf_ReadsWhatBinaryFormatterWrote() {
    var descriptor = new NrbfFormatDescriptor();

    var text = ReferenceVectorFixture.Load("nrbf-string.nrbf");
    Assert.That(ReferenceVectorFixture.ExtractText(descriptor, text, "objects/%401"), Is.EqualTo("hello"));

    var table = ReferenceVectorFixture.Load("nrbf-hashtable.nrbf");
    var tableEntries = ReferenceVectorFixture.List(descriptor, table);
    Assert.Multiple(() => {
      Assert.That(tableEntries.Any(e => e.IsDirectory && e.Kind == "System.Collections.Hashtable"), Is.True);
      // A Hashtable serialises as its Keys and Values object arrays, reached through object references.
      Assert.That(tableEntries.Any(e => e.Name == "objects/%401/Keys" && e.Kind == "object-reference"), Is.True);
      Assert.That(
        new[] { "objects/%402/[000000]", "objects/%402/[000001]" }
          .Select(x => ReferenceVectorFixture.ExtractText(descriptor, table, x)),
        Is.EquivalentTo(new[] { "count", "name" }));
      Assert.That(
        new[] { "objects/%403/[000000]", "objects/%403/[000001]" }
          .Select(x => ReferenceVectorFixture.ExtractText(descriptor, table, x)),
        Is.EquivalentTo(new[] { "7", "value" }));
    });

    var array = ReferenceVectorFixture.Load("nrbf-object-array.nrbf");
    Assert.Multiple(() => {
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, array, "objects/%401/[000000]"), Is.EqualTo("first"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, array, "objects/%401/[000001]"), Is.EqualTo("42"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, array, "objects/%401/[000002]"), Is.EqualTo("true"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, array, "objects/%401/[000003]"), Is.EqualTo("null"));
      Assert.That(ReferenceVectorFixture.ExtractText(descriptor, array, "objects/%401/[000004]"), Is.EqualTo("1.5"));
    });
  }

  // ----------------------------------------------------------------------------- registry hives

  /// <summary>
  /// The whole hive, not a corner of it. A Windows 9x <c>USER.DAT</c> marks its root key, and every
  /// freed RGDB slot, with the 0xffff "no key-name entry" sentinel, and it addresses key names by
  /// the identifier each RGDB record stores rather than by the record's position in its block. A
  /// reader that treats the sentinel as corruption aborts on the first key it meets — the root —
  /// and one that indexes positionally silently resolves 61 of these keys to another key's name.
  /// The counts are from an independent walk of the same file, not from this reader.
  /// </summary>
  [Test]
  public void Creg_ListsTheWholeRealWindows9xHive() {
    var descriptor = new CregFormatDescriptor();
    var bytes = ReferenceVectorFixture.Load("windows9x-user.dat.gz");
    var entries = ReferenceVectorFixture.List(descriptor, bytes);

    const string mountPoints = ".DEFAULT/Software/Microsoft/Windows/CurrentVersion/Explorer/MountPoints";
    Assert.Multiple(() => {
      Assert.That(entries, Has.Count.EqualTo(2305), "787 named keys below the root plus 1518 values");
      Assert.That(entries.Count(e => e.IsDirectory), Is.EqualTo(787));

      Assert.That(entries.Any(e => e.IsDirectory && e.Name == ".DEFAULT"), Is.True);
      Assert.That(entries.Any(e => e.IsDirectory && e.Name == "Software"), Is.True);

      // Positional lookup swaps these two siblings for each other.
      Assert.That(entries.Any(e => e.IsDirectory && e.Name == $"{mountPoints}/A/_Autorun"), Is.True);
      Assert.That(entries.Any(e => e.IsDirectory && e.Name == $"{mountPoints}/A/_DIL"), Is.True);
      Assert.That(entries.Any(e => e.Name == $"{mountPoints}/A/_Autorun/LastUpdate" && e.Kind == "REG_DWORD"), Is.True);

      Assert.That(
        ReferenceVectorFixture.ExtractText(descriptor, bytes, $"{mountPoints}/A/_Autorun/LastUpdate"),
        Is.EqualTo("0x00027D8E"));
      Assert.That(
        ReferenceVectorFixture.ExtractText(descriptor, bytes, ".DEFAULT/Control%20Panel/International/Locale"),
        Is.EqualTo("00000409"));
      Assert.That(
        ReferenceVectorFixture.ExtractText(descriptor, bytes, ".DEFAULT/Control%20Panel/International/geo/nation"),
        Is.EqualTo("244"));
    });
  }

  /// <summary>
  /// The NT-family counterpart, a real <c>NTUSER.DAT</c>. Its predecessor drove <c>reg.exe save</c>
  /// at test time, which needs <c>SeBackupPrivilege</c> and therefore reported a red on every host
  /// that does not have it rather than reporting "not validated".
  /// </summary>
  [Test]
  public void Regf_ListsTheWholeRealWindowsNtHive() {
    var descriptor = new RegfFormatDescriptor();
    var bytes = ReferenceVectorFixture.Load("windows-nt-ntuser.dat.gz");
    var entries = ReferenceVectorFixture.List(descriptor, bytes);

    Assert.Multiple(() => {
      Assert.That(entries, Has.Count.EqualTo(3906), "1596 named keys below the root plus 2310 values");
      Assert.That(entries.Count(e => e.IsDirectory), Is.EqualTo(1596));

      Assert.That(
        ReferenceVectorFixture.ExtractText(descriptor, bytes, "AppEvents/EventLabels/.Default/%40"),
        Is.EqualTo("Default Beep"));
      Assert.That(
        ReferenceVectorFixture.ExtractText(descriptor, bytes, "AppEvents/EventLabels/CCSelect/DispFileName"),
        Is.EqualTo("@ieframe.dll,-10323"));
      Assert.That(
        ReferenceVectorFixture.ExtractText(descriptor, bytes, "Environment/TEMP"),
        Is.EqualTo("%USERPROFILE%\\AppData\\Local\\Temp"));
      Assert.That(
        entries.Single(e => e.Name == "Environment/TEMP").Kind, Is.EqualTo("REG_EXPAND_SZ"));
      Assert.That(
        ReferenceVectorFixture.ExtractText(descriptor, bytes, "Control%20Panel/Desktop/Wallpaper"),
        Is.EqualTo("C:\\Windows\\Web\\Wallpaper\\Windows\\img0.jpg"));
      Assert.That(
        entries.Single(e => e.Name == "Console/ColorTable00").Kind, Is.EqualTo("REG_DWORD"));
    });
  }
}
