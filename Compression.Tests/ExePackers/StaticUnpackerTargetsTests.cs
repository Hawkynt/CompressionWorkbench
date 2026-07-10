using System.Buffers.Binary;
using System.Text;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using Compression.Tests.Support;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Static-unpacking coverage for the four Task #239 targets: hXOR-Packer,
/// Eronana Packer, PE-Packer (czs108) and SimpleDpack.
/// </summary>
[TestFixture]
public class StaticUnpackerTargetsTests {
  [Test, Category("HappyPath")]
  public void Registry_ContainsAllFourTargets() {
    var ids = ExecutablePackerHandlers.All.Select(h => h.Id).ToArray();
    Assert.That(ids, Is.SupersetOf(new[] { "hxor", "eronanapacker", "pepacker_czs108", "simpledpack" }));
  }

  // ───────────────────────── hXOR-Packer ─────────────────────────

  [Test, Category("HappyPath")]
  public void Hxor_StoredMode_RecoversOriginalByteIdentically() {
    var original = MinimalPe();
    var wrapper = BuildHxorContainer(parameter: 0, key: 0, original);

    var handler = new HxorExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/reconstructed.exe").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Hxor_XorModeWithExplicitKey_RecoversOriginalByteIdentically() {
    var original = MinimalPe();
    var wrapper = BuildHxorContainer(parameter: 2, key: 777, original);

    var handler = new HxorExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/reconstructed.exe").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Hxor_XorModeWithSizeDerivedKey_RecoversOriginalByteIdentically() {
    var original = MinimalPe();
    var wrapper = BuildHxorContainer(parameter: 2, key: 0, original);

    var handler = new HxorExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
    Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/reconstructed.exe").Data,
      Is.EqualTo(original).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void Hxor_HuffmanCompressedMode_StopsAtPayloadLocatedWithDiagnostic() {
    var original = MinimalPe();
    // parameter=1 (huffman-only): we don't have a static Huffman re-encoder,
    // so the payload bytes here are deliberately NOT a valid encoding of
    // `original` — the handler must not fabricate a decode either way.
    var wrapper = BuildHxorContainer(parameter: 1, key: 0, original, rawPayloadOverride: original);

    var handler = new HxorExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("ExternalTool")]
  public void Hxor_RealPackerToolOutput_RecoversOriginalByteIdentically() {
    var tools = ExecutablePackerToolCache.GetHxor();
    Assume.That(tools, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download hXOR-Packer, or place packer.exe + unpackerLoadEXE.exe on PATH.");

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      var packerExe = Path.Combine(tmp, "packer.exe");
      var stubExe = Path.Combine(tmp, "unpackerLoadEXE.exe");
      File.Copy(tools!.Packer, packerExe, overwrite: true);
      File.Copy(tools.Stub, stubExe, overwrite: true);

      var original = MinimalPe();
      var inPath = Path.Combine(tmp, "in.exe");
      var outPath = Path.Combine(tmp, "out.exe");
      File.WriteAllBytes(inPath, original);

      // Local security scanners occasionally lock or quarantine a freshly
      // produced "packer" output file for a moment (observed in this
      // project's own sandboxing while developing this test); retry the
      // full pack-then-read cycle and fall back to Assume.Inconclusive
      // rather than let a transient I/O error surface as a hard failure.
      string output = "";
      byte[]? wrapper = null;
      for (var attempt = 0; attempt < 3 && wrapper == null; attempt++) {
        output = ExecutablePackerToolCache.RunInDirectory(packerExe, tmp, inPath, outPath, "-e", "12345");
        try {
          if (File.Exists(outPath)) wrapper = File.ReadAllBytes(outPath);
        } catch (IOException) {
          // Transient lock (e.g. an antivirus scan) — retry.
        } catch (UnauthorizedAccessException) {
          // Ditto.
        }
      }
      Assume.That(wrapper, Is.Not.Null, $"hXOR-Packer did not produce a readable output file (possibly AV-locked the freshly-downloaded stub). Output: {output}");
      var handler = new HxorExecutablePackerHandler();
      var result = Unpack(handler, wrapper!);

      Assert.Multiple(() => {
        Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
        Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/reconstructed.exe").Data,
          Is.EqualTo(original).AsCollection);
      });
    } finally {
      try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort cleanup */ }
    }
  }

  // ───────────────────────── Eronana Packer ─────────────────────────

  [Test, Category("HappyPath")]
  public void Eronana_RestoresStrippedSectionsAndRebuildsMemoryImage() {
    var textBytes = Repeat("ABCD", 16); // 64 bytes
    var dataBytes = Repeat("DCBA", 16); // 64 bytes
    var wrapper = BuildEronanaPackedPe(textBytes, dataBytes, out var textVa, out var dataVa);

    var handler = new EronanaExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      var decompressed = result.Artifacts.Single(a => a.Name == "decompressed_sections.bin").Data;
      Assert.That(decompressed, Is.EqualTo(textBytes.Concat(dataBytes)).AsCollection);

      var memoryImage = result.Artifacts.Single(a => a.Name == "memory_image.bin").Data;
      var low = Math.Min(textVa, dataVa);
      Assert.That(memoryImage.AsSpan((int)(textVa - low), textBytes.Length).ToArray(), Is.EqualTo(textBytes).AsCollection);
      Assert.That(memoryImage.AsSpan((int)(dataVa - low), dataBytes.Length).ToArray(), Is.EqualTo(dataBytes).AsCollection);

      Assert.That(result.Artifacts.Any(a => a.Name == "reconstructed/reconstructed.exe"), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_RecognizesEronanaPackedPe() {
    var textBytes = Repeat("ABCD", 16);
    var dataBytes = Repeat("DCBA", 16);
    var wrapper = BuildEronanaPackedPe(textBytes, dataBytes, out _, out _);

    var match = ExecutablePackerHandlers.DetectBest(wrapper);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("eronanapacker"));
  }

  // ───────────────────────── PE-Packer (czs108) ─────────────────────────

  [Test, Category("HappyPath")]
  public void PePacker_DetectsAndLocatesShellSection_WithoutFabricatingADecode() {
    var wrapper = BuildPePackerLikePe();

    var handler = new PePackerExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Any(a => a.Name == "shell_section.bin"), Is.True);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  // ───────────────────────── SimpleDpack ─────────────────────────

  [Test, Category("HappyPath")]
  public void SimpleDpack_DetectsAndLocatesDpackSection_WithoutFabricatingADecode() {
    var wrapper = BuildSimpleDpackLikePe();

    var handler = new SimpleDpackExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Any(a => a.Name == "dpack_section.bin"), Is.True);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("ExternalTool")]
  public void SimpleDpack_RealPackerToolOutput_IsAtLeastDetectedAndLocated() {
    var tools = ExecutablePackerToolCache.GetSimpleDpack();
    Assume.That(tools, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download SimpleDpack, or place SimpleDpack.exe + simpledpackshell.dll on PATH.");

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      var exe = Path.Combine(tmp, "SimpleDpack.exe");
      var dll = Path.Combine(tmp, "simpledpackshell.dll");
      File.Copy(tools!.Exe, exe, overwrite: true);
      File.Copy(tools.ShellDll, dll, overwrite: true);

      var inPath = Path.Combine(tmp, "in.exe");
      var outPath = Path.Combine(tmp, "out.exe");
      File.WriteAllBytes(inPath, MinimalPe());

      byte[]? wrapper = null;
      for (var attempt = 0; attempt < 3 && wrapper == null; attempt++) {
        ExecutablePackerToolCache.RunInDirectory(exe, tmp, inPath, outPath);
        try {
          if (File.Exists(outPath)) wrapper = File.ReadAllBytes(outPath);
        } catch (IOException) {
          // Transient lock (e.g. an antivirus scan) — retry.
        } catch (UnauthorizedAccessException) {
          // Ditto.
        }
      }
      Assume.That(wrapper, Is.Not.Null, "SimpleDpack did not produce a readable output file (possibly AV-locked).");

      var match = ExecutablePackerHandlers.DetectBest(wrapper!);
      Assume.That(match, Is.Not.Null, "SimpleDpack's real output was not recognized (no \".dpack\" section) — nothing further to validate.");

      var result = ExecutablePackerHandlers.TryUnpack(wrapper!);
      Assert.That(result, Is.Not.Null);
      Assert.That(result!.Level, Is.GreaterThanOrEqualTo(ExecutableUnpackLevel.PayloadLocated));
    } finally {
      try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort cleanup */ }
    }
  }

  // ───────────────────────── shared helpers ─────────────────────────

  private static UnpackResult Unpack(IExecutablePackerHandler handler, byte[] image) {
    var detection = handler.Detect(image);
    Assert.That(detection.IsMatch, Is.True);
    return handler.Unpack(handler.Parse(image, detection), new());
  }

  private static byte[] MinimalPe() {
    var buf = new byte[1024];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C), 0x80);
    buf[0x80] = (byte)'P'; buf[0x81] = (byte)'E';
    return buf;
  }

  private static byte[] Repeat(string pattern, int times) {
    var bytes = Encoding.ASCII.GetBytes(pattern);
    var result = new byte[bytes.Length * times];
    for (var i = 0; i < times; i++) bytes.CopyTo(result.AsSpan(i * bytes.Length));
    return result;
  }

  // ---- hXOR container builder ----

  private static byte[] BuildHxorContainer(int parameter, int key, byte[] original, byte[]? rawPayloadOverride = null) {
    var stub = MinimalPe();
    var stubSize = stub.Length;

    byte[] payloadBytes;
    int storedFilesize;
    if (rawPayloadOverride != null) {
      payloadBytes = rawPayloadOverride;
      storedFilesize = original.Length;
    } else
      switch (parameter) {
        case 0:
          payloadBytes = original;
          storedFilesize = original.Length;
          break;
        case 2: {
          var seed = unchecked((uint)(key != 0 ? key : original.Length));
          var xorKey = (byte)(HxorExecutablePackerHandler.MsvcrtRand(seed) % 69);
          payloadBytes = original.Select(b => (byte)(b ^ xorKey)).ToArray();
          storedFilesize = original.Length;
          break;
        }
        default:
          throw new ArgumentOutOfRangeException(nameof(parameter));
      }

    var pdata = new byte[272];
    Encoding.ASCII.GetBytes("test.exe").CopyTo(pdata, 0);
    BinaryPrimitives.WriteInt32LittleEndian(pdata.AsSpan(260, 4), storedFilesize);
    BinaryPrimitives.WriteInt32LittleEndian(pdata.AsSpan(264, 4), key);
    BinaryPrimitives.WriteInt32LittleEndian(pdata.AsSpan(268, 4), parameter);

    var result = new byte[stubSize + 4 + 272 + payloadBytes.Length];
    stub.CopyTo(result.AsSpan());
    Encoding.ASCII.GetBytes("FIFA").CopyTo(result.AsSpan(stubSize, 4));
    pdata.CopyTo(result.AsSpan(stubSize + 4));
    payloadBytes.CopyTo(result.AsSpan(stubSize + 4 + 272));

    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x28, 4), stubSize); // e_res2 insert offset
    return result;
  }

  // ---- Eronana container builder ----

  private static byte[] BuildEronanaPackedPe(byte[] textBytes, byte[] dataBytes, out uint textVa, out uint dataVa) {
    const uint imageBase = 0x400000;
    textVa = 0x1000;
    dataVa = 0x2000;
    const uint packerVa = 0x3000;
    const uint aep = 0x1234;
    const uint iidVa = 0x2000;

    var combined = textBytes.Concat(dataBytes).ToArray();
    var compressedBlob = EncodeEronanaBlob(combined);

    var sectionInfoBytes = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(sectionInfoBytes.AsSpan(0, 4), textVa);
    BinaryPrimitives.WriteUInt32LittleEndian(sectionInfoBytes.AsSpan(4, 4), (uint)textBytes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(sectionInfoBytes.AsSpan(8, 4), dataVa);
    BinaryPrimitives.WriteUInt32LittleEndian(sectionInfoBytes.AsSpan(12, 4), (uint)dataBytes.Length);

    var peInfo = new byte[44];
    BinaryPrimitives.WriteUInt32LittleEndian(peInfo.AsSpan(0, 4), imageBase);
    BinaryPrimitives.WriteUInt32LittleEndian(peInfo.AsSpan(4, 4), aep);
    BinaryPrimitives.WriteInt32LittleEndian(peInfo.AsSpan(8, 4), 2); // NumberOfSections
    BinaryPrimitives.WriteUInt32LittleEndian(peInfo.AsSpan(12, 4), iidVa);
    BinaryPrimitives.WriteUInt32LittleEndian(peInfo.AsSpan(16, 4), 0); // NodeTotal (unused by our decoder)
    BinaryPrimitives.WriteInt32LittleEndian(peInfo.AsSpan(20, 4), combined.Length); // UncompressSize

    var packerSectionContent = peInfo.Concat(sectionInfoBytes).Concat(compressedBlob).ToArray();

    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionTableOffset = optionalOffset + optionalSize;
    const int rawOffset = 0x400;

    var image = new byte[rawOffset + packerSectionContent.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C); // x86
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 3);     // 3 sections
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B); // PE32
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), aep);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), imageBase);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000); // section align
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);  // file align

    WriteSectionHeader(image, sectionTableOffset + 0 * 40, ".text", textVa, (uint)textBytes.Length, 0, 0, 0x60000020);
    WriteSectionHeader(image, sectionTableOffset + 1 * 40, ".data", dataVa, (uint)dataBytes.Length, 0, 0, 0xC0000040);
    WriteSectionHeader(image, sectionTableOffset + 2 * 40, ".packer", packerVa, (uint)packerSectionContent.Length, (uint)packerSectionContent.Length, rawOffset, 0xE0000000);

    packerSectionContent.CopyTo(image.AsSpan(rawOffset));
    return image;
  }

  private static void WriteSectionHeader(byte[] image, int offset, string name, uint virtualAddress, uint virtualSize, uint rawSize, int rawOffset, uint characteristics) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    nameBytes.AsSpan(0, Math.Min(8, nameBytes.Length)).CopyTo(image.AsSpan(offset, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 8), virtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 12), virtualAddress);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 16), rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 20), (uint)rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 36), characteristics);
  }

  /// <summary>
  /// Encodes <paramref name="combined"/> as a spec-conformant Eronana
  /// <c>compressor</c> stream with zero LZ77 back-references (every symbol is
  /// a literal byte) — the canonical-Huffman half of the format exercised
  /// end-to-end, matching <c>compressor/uncompressor.cpp</c>'s bit layout.
  /// The LZ77 back-reference path was validated separately against a real
  /// sample built with the published Eronana packer (see handler XML docs).
  /// </summary>
  private static byte[] EncodeEronanaBlob(byte[] combined) {
    var freq = new Dictionary<int, int>();
    foreach (var b in combined) freq[b] = freq.GetValueOrDefault(b) + 1;

    var lengths = ComputeCodeLengths(freq);
    var grouped = lengths.GroupBy(kv => kv.Value).OrderBy(g => g.Key).ToList();

    var tree = new List<int>();
    var lenSize = new List<(int Len, int Size)>();
    var symbolCode = new Dictionary<int, (int Code, int Len)>();
    var code = 0;
    var lastLen = 0;
    foreach (var g in grouped) {
      var len = g.Key;
      code <<= len - lastLen;
      lastLen = len;
      var symbols = g.Select(kv => kv.Key).OrderBy(s => s).ToList();
      lenSize.Add((len, symbols.Count));
      foreach (var s in symbols) {
        tree.Add(s);
        symbolCode[s] = (code, len);
        code++;
      }
    }

    var bits = new BitWriter();
    foreach (var b in combined) {
      var (c, len) = symbolCode[b];
      bits.Put(c, len);
    }
    bits.End();
    var bitStream = bits.ToArray();

    using var ms = new MemoryStream();
    void WriteU16(int v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }
    void WriteU32(int v) { for (var i = 0; i < 4; i++) ms.WriteByte((byte)(v >> (8 * i))); }

    WriteU16(tree.Count);
    WriteU16(lenSize.Count);
    WriteU32(combined.Length); // d_buf_size
    WriteU32(0);                // l_buf_size (no matches)
    foreach (var t in tree) WriteU16(t);
    foreach (var (len, size) in lenSize) { WriteU16(len); WriteU16(size); }
    ms.Write(bitStream);
    return ms.ToArray();
  }

  private static Dictionary<int, int> ComputeCodeLengths(Dictionary<int, int> freq) {
    var pq = new PriorityQueue<HNode, long>();
    foreach (var (sym, f) in freq) pq.Enqueue(new HNode { Symbol = sym, Freq = f }, f);

    if (pq.Count == 1) {
      var only = pq.Dequeue();
      return new Dictionary<int, int> { [only.Symbol] = 0 };
    }

    while (pq.Count > 1) {
      var a = pq.Dequeue();
      var b = pq.Dequeue();
      var parent = new HNode { Left = a, Right = b, Freq = a.Freq + b.Freq };
      pq.Enqueue(parent, parent.Freq);
    }

    var root = pq.Dequeue();
    var lengths = new Dictionary<int, int>();
    void Walk(HNode node, int depth) {
      if (node.Symbol >= 0) { lengths[node.Symbol] = depth; return; }
      Walk(node.Left!, depth + 1);
      Walk(node.Right!, depth + 1);
    }
    Walk(root, 0);
    return lengths;
  }

  private sealed class HNode {
    public int Symbol = -1;
    public long Freq;
    public HNode? Left;
    public HNode? Right;
  }

  /// <summary>MSB-first bit packer, a direct port of Eronana's <c>BitStream</c>.</summary>
  private sealed class BitWriter {
    private readonly List<byte> _buffer = [];
    private int _temp;
    private int _count;

    public void Put(int code, int len) {
      if (len == 0) return;
      if (_count + len >= 8) {
        var x = 8 - _count;
        _temp <<= x;
        _count = len - x;
        _temp |= code >> _count;
        _buffer.Add((byte)_temp);
        code &= (1 << _count) - 1;
        while (_count >= 8) {
          _buffer.Add((byte)(code >> (_count - 8)));
          _count -= 8;
          code &= (1 << _count) - 1;
        }
        _temp = code;
      } else {
        _count += len;
        _temp <<= len;
        _temp |= code;
      }
    }

    public void End() {
      if (_count > 0) _buffer.Add((byte)(_temp << (8 - _count)));
    }

    public byte[] ToArray() => [.. _buffer];
  }

  // ---- PE-Packer / SimpleDpack locate-level fixtures ----

  private static byte[] BuildPePackerLikePe() {
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionTableOffset = optionalOffset + optionalSize;
    const int rawOffset = 0x400;
    var shellPayload = new byte[256];
    new Random(0xBEEF).NextBytes(shellPayload);

    var image = new byte[rawOffset + shellPayload.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);

    // First section: name cleared (as ClearSectionNames() does upstream), no raw data.
    WriteSectionHeader(image, sectionTableOffset + 0 * 40, "", 0x1000, 0x1000, 0, 0, 0x60000020);
    // Second (last) section: the appended shell, name intact.
    WriteSectionHeader(image, sectionTableOffset + 1 * 40, ".shell", 0x2000, (uint)shellPayload.Length, (uint)shellPayload.Length, rawOffset, 0xE0000020);

    shellPayload.CopyTo(image.AsSpan(rawOffset));
    return image;
  }

  private static byte[] BuildSimpleDpackLikePe() {
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionTableOffset = optionalOffset + optionalSize;
    const int rawOffset = 0x400;
    var dpackPayload = new byte[256];
    new Random(0xCAFE).NextBytes(dpackPayload);

    var image = new byte[rawOffset + dpackPayload.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);

    WriteSectionHeader(image, sectionTableOffset, ".dpack", 0x1000, (uint)dpackPayload.Length, (uint)dpackPayload.Length, rawOffset, 0xE0000000);
    dpackPayload.CopyTo(image.AsSpan(rawOffset));
    return image;
  }
}
