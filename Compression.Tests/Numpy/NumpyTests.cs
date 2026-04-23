using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FileFormat.Numpy;

namespace Compression.Tests.Numpy;

[TestFixture]
public class NumpyTests {

  // ── NPY helpers ─────────────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal v1 NPY file with the provided dtype/shape header and body.
  /// The header dict is padded with spaces + newline so the total (magic + version +
  /// header-len + dict) is a multiple of 64.
  /// </summary>
  private static byte[] BuildNpyV1(string dtype, string shape, bool fortran, byte[] body) {
    var dict = $"{{'descr': '{dtype}', 'fortran_order': {(fortran ? "True" : "False")}, 'shape': {shape}, }}";
    var preamble = 10; // 6 magic + 2 version + 2 header-len
    // Pad to 64-byte alignment, reserving one byte for a trailing newline.
    var targetLen = ((preamble + dict.Length + 1 + 63) / 64) * 64;
    var pad = targetLen - preamble - dict.Length - 1;
    var headerText = dict + new string(' ', pad) + "\n";
    var headerLen = headerText.Length;

    var buf = new byte[preamble + headerLen + body.Length];
    buf[0] = 0x93; buf[1] = (byte)'N'; buf[2] = (byte)'U'; buf[3] = (byte)'M'; buf[4] = (byte)'P'; buf[5] = (byte)'Y';
    buf[6] = 1; buf[7] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)headerLen);
    Encoding.ASCII.GetBytes(headerText).CopyTo(buf.AsSpan(preamble));
    body.CopyTo(buf.AsSpan(preamble + headerLen));
    return buf;
  }

  private static byte[] BuildNpyV2(string dtype, string shape, byte[] body) {
    var dict = $"{{'descr': '{dtype}', 'fortran_order': False, 'shape': {shape}, }}";
    var preamble = 12;
    var targetLen = ((preamble + dict.Length + 1 + 63) / 64) * 64;
    var pad = targetLen - preamble - dict.Length - 1;
    var headerText = dict + new string(' ', pad) + "\n";
    var headerLen = headerText.Length;

    var buf = new byte[preamble + headerLen + body.Length];
    buf[0] = 0x93; buf[1] = (byte)'N'; buf[2] = (byte)'U'; buf[3] = (byte)'M'; buf[4] = (byte)'P'; buf[5] = (byte)'Y';
    buf[6] = 2; buf[7] = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), (uint)headerLen);
    Encoding.ASCII.GetBytes(headerText).CopyTo(buf.AsSpan(preamble));
    body.CopyTo(buf.AsSpan(preamble + headerLen));
    return buf;
  }

  // ── Reader tests ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void NpyReader_V1_ParsesDtypeShapeAndBody() {
    var body = new byte[12];
    for (var i = 0; i < body.Length; i++) body[i] = (byte)i;
    var data = BuildNpyV1("<f4", "(3,)", fortran: false, body);

    var arr = NpyReader.Read(data);
    Assert.Multiple(() => {
      Assert.That(arr.MajorVersion, Is.EqualTo(1));
      Assert.That(arr.Dtype, Is.EqualTo("<f4"));
      Assert.That(arr.Shape, Is.EqualTo("(3,)"));
      Assert.That(arr.FortranOrder, Is.False);
      Assert.That(arr.ArrayBytes, Is.EqualTo(body).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void NpyReader_V2_ParsesHeader() {
    var body = new byte[8];
    var data = BuildNpyV2("|u1", "(2, 4)", body);
    var arr = NpyReader.Read(data);
    Assert.That(arr.MajorVersion, Is.EqualTo(2));
    Assert.That(arr.Dtype, Is.EqualTo("|u1"));
    Assert.That(arr.Shape, Is.EqualTo("(2, 4)"));
  }

  [Test, Category("HappyPath")]
  public void NpyReader_FortranOrder_Detected() {
    var data = BuildNpyV1("<i4", "(4,)", fortran: true, new byte[16]);
    var arr = NpyReader.Read(data);
    Assert.That(arr.FortranOrder, Is.True);
  }

  [Test, Category("EdgeCase")]
  public void NpyReader_BadMagic_Throws() {
    var data = new byte[32];
    Assert.That(() => NpyReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void NpyReader_TruncatedHeader_Throws() {
    var data = new byte[5];
    Assert.That(() => NpyReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  // ── Descriptor tests ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void NpyDescriptor_ListEmitsMetadataHeaderAndArray() {
    var data = BuildNpyV1("<f4", "(3,)", fortran: false, new byte[12]);
    using var ms = new MemoryStream(data);
    var entries = new NpyFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("header.bin"));
    Assert.That(names, Does.Contain("array.bin"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NpyDescriptor_ExtractWritesArrayBin() {
    var body = new byte[8];
    for (var i = 0; i < body.Length; i++) body[i] = (byte)(0xA0 + i);
    var data = BuildNpyV1("<f4", "(2,)", fortran: false, body);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new NpyFormatDescriptor().Extract(ms, tmp, null, null);
      var arrayFile = Path.Combine(tmp, "array.bin");
      Assert.That(File.Exists(arrayFile), Is.True);
      Assert.That(File.ReadAllBytes(arrayFile), Is.EqualTo(body).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  // ── NPZ tests ───────────────────────────────────────────────────────

  private static byte[] BuildNpz(params (string Name, byte[] Data)[] entries) {
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
      foreach (var (name, data) in entries) {
        var e = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = e.Open();
        s.Write(data);
      }
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void NpzDescriptor_ListEmitsMetadataAndNpyEntries() {
    var npy1 = BuildNpyV1("<f4", "(3,)", fortran: false, new byte[12]);
    var npy2 = BuildNpyV1("<i4", "(2,)", fortran: false, new byte[8]);
    var npz = BuildNpz(("arr_0.npy", npy1), ("arr_1.npy", npy2));

    using var ms = new MemoryStream(npz);
    var entries = new NpzFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("arr_0.npy"));
      Assert.That(names, Does.Contain("arr_1.npy"));
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NpzDescriptor_ExtractPutsNpysOnDisk() {
    var npy1 = BuildNpyV1("<f4", "(3,)", fortran: false, new byte[12]);
    var npz = BuildNpz(("arr_0.npy", npy1));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(npz);
      new NpzFormatDescriptor().Extract(ms, tmp, null, null);
      var extracted = Path.Combine(tmp, "arr_0.npy");
      Assert.That(File.Exists(extracted), Is.True);
      Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(npy1).AsCollection);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void NpzDescriptor_MalformedZip_EmitsErrorMetadata() {
    var bogus = new byte[16];
    bogus[0] = (byte)'P'; bogus[1] = (byte)'K'; bogus[2] = 0x03; bogus[3] = 0x04;
    using var ms = new MemoryStream(bogus);
    var entries = new NpzFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }
}
