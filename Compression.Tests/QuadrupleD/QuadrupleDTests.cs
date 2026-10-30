using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.QuadrupleD;

namespace Compression.Tests.QuadrupleD;

/// <summary>
/// Quadruple D (QDA0). The real archives found are game data (the dexvert sample set) and cannot be
/// redistributed, so the real-file check runs on a directory named by <c>CWB_QDA_SAMPLES</c>.
/// No program that reads the format is available, so it checks the decoded files against
/// themselves: every BMP's header records its own file size and every WAV's RIFF header its own
/// length, which a mis-decoded file does not keep.
/// The structural tests use archives this test builds with its own byte pair encoder.
/// </summary>
[TestFixture]
public class QuadrupleDTests {

  /// <summary>Gage-style byte pair encoder: one block, pairs replaced while unused codes remain.</summary>
  private static byte[] BytePairEncode(byte[] data) {
    if (data.Length == 0) return [];
    var left = new byte[256];
    var right = new byte[256];
    var isPair = new bool[256];
    var buffer = new List<byte>(data);
    // a code is taken once it occurs anywhere: in the data or as a pair code
    var used = new bool[256];
    foreach (var b in data) used[b] = true;
    for (;;) {
      var free = Array.IndexOf(used, false);
      if (free < 0) break;
      var counts = new Dictionary<(byte, byte), int>();
      for (var i = 0; i + 1 < buffer.Count; ++i) counts[(buffer[i], buffer[i + 1])] = counts.GetValueOrDefault((buffer[i], buffer[i + 1])) + 1;
      if (counts.Count == 0) break;
      var best = counts.MaxBy(kv => kv.Value);
      if (best.Value < 3) break;
      var next = new List<byte>(buffer.Count);
      for (var i = 0; i < buffer.Count; ++i) {
        if (i + 1 < buffer.Count && buffer[i] == best.Key.Item1 && buffer[i + 1] == best.Key.Item2) {
          next.Add((byte)free);
          ++i;
        } else next.Add(buffer[i]);
      }
      left[free] = best.Key.Item1;
      right[free] = best.Key.Item2;
      isPair[free] = true;
      used[free] = true;
      buffer = next;
    }

    var o = new List<byte>();
    for (var c = 0; c < 256;) {
      var literals = 0;
      while (c + literals < 256 && !isPair[c + literals]) ++literals;
      if (c + literals == 256 && literals <= 128) {
        o.Add((byte)(127 + literals));
        break;
      }
      if (literals > 128) {
        o.Add(255); // skip 128, introduce one more literal
        o.Add((byte)(c + 128));
        c += 129;
        continue;
      }
      if (literals > 0) o.Add((byte)(127 + literals));
      else o.Add(0);
      c += literals;
      o.Add(left[c]);
      o.Add(right[c]);
      ++c;
      if (c == 256) break;
    }
    var size = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)buffer.Count);
    o.AddRange(size);
    o.AddRange(buffer);
    return [.. o];
  }

  private static byte[] Archive(bool compressed, params (string Name, byte[] Data)[] files) {
    var stored = files.Select(f => compressed ? BytePairEncode(f.Data) : f.Data).ToArray();
    var header = new byte[0x100 + files.Length * 268];
    BinaryPrimitives.WriteUInt32LittleEndian(header, compressed ? 1u : 0u);
    "QDA0"u8.CopyTo(header.AsSpan(4));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)files.Length);
    var offset = header.Length;
    for (var i = 0; i < files.Length; ++i) {
      var r = header.AsSpan(0x100 + i * 268);
      BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)offset);
      BinaryPrimitives.WriteUInt32LittleEndian(r[4..], (uint)stored[i].Length);
      BinaryPrimitives.WriteUInt32LittleEndian(r[8..], (uint)files[i].Data.Length);
      Encoding.ASCII.GetBytes(files[i].Name).CopyTo(r[12..]);
      offset += stored[i].Length;
    }
    return [.. header, .. stored.SelectMany(s => s)];
  }

  private static byte[] Text(int length) {
    var t = Encoding.ASCII.GetBytes("the rain in spain stays mainly in the plain. ");
    return Enumerable.Range(0, length).Select(i => t[i % t.Length]).ToArray();
  }

  private static byte[] AllBytes() => Enumerable.Range(0, 1024).Select(i => (byte)(i * 7)).ToArray();

  [TestCase(true), TestCase(false), Category("RoundTrip")]
  public void GivenArchive_WhenRead_ThenEveryFileIsRecovered(bool compressed) {
    (string, byte[])[] files = [("a.txt", Text(5000)), ("empty.bin", []), ("one.bin", [42]), ("all.bin", AllBytes())];
    var archive = Archive(compressed, files);
    var entries = QuadrupleDArchive.List(archive);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(files.Select(f => f.Item1)));
    for (var i = 0; i < files.Length; ++i) Assert.That(QuadrupleDArchive.Extract(archive, entries[i]), Is.EqualTo(files[i].Item2), files[i].Item1);
    if (compressed) Assert.That(entries[0].StoredSize, Is.LessThan(entries[0].Size));
  }

  [Test, Category("HappyPath")]
  public void GivenDescriptor_WhenListed_ThenNamesSizesAndMethodAreReported() {
    var d = new QuadrupleDFormatDescriptor();
    var list = d.List(new MemoryStream(Archive(true, ("bg.bmp", Text(3000)))), null);
    Assert.That(list.Single().Name, Is.EqualTo("bg.bmp"));
    Assert.That(list.Single().OriginalSize, Is.EqualTo(3000));
    Assert.That(list.Single().Method, Is.EqualTo("BPE"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
  }

  [Test, Category("Exception")]
  public void GivenNotQda_WhenListed_ThenRefused() {
    Assert.That(() => QuadrupleDArchive.List("PK\x03\x04............"u8), Throws.InstanceOf<InvalidDataException>());
    var bad = Archive(false, ("x", [1, 2, 3]));
    BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(8), 1000);
    Assert.That(() => QuadrupleDArchive.List(bad), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenTruncatedBlock_WhenExtracted_ThenRefused() {
    var archive = Archive(true, ("a.txt", Text(4000)));
    var e = QuadrupleDArchive.List(archive).Single();
    var shorter = e with { StoredSize = e.StoredSize - 10 };
    Assert.That(() => QuadrupleDArchive.Extract(archive, shorter), Throws.InstanceOf<InvalidDataException>());
    var larger = e with { Size = e.Size + 1 };
    Assert.That(() => QuadrupleDArchive.Extract(archive, larger), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ThemVsUs")]
  public void GivenLocalSampleArchives_WhenDecoded_ThenEveryBmpAndWavHeaderMatchesItsSize() {
    var samples = Environment.GetEnvironmentVariable("CWB_QDA_SAMPLES");
    if (string.IsNullOrEmpty(samples) || !Directory.Exists(samples)) Assert.Ignore("CWB_QDA_SAMPLES is not set.");
    int checkedFiles = 0, compressedFiles = 0;
    foreach (var path in Directory.EnumerateFiles(samples)) {
      var data = File.ReadAllBytes(path);
      if (!QuadrupleDArchive.IsQuadrupleD(data)) continue;
      foreach (var e in QuadrupleDArchive.List(data)) {
        var file = QuadrupleDArchive.Extract(data, e);
        Assert.That(file, Has.Length.EqualTo(e.Size));
        // some BMP writers pad the file by up to 4 bytes beyond the size they record
        if (file.AsSpan().StartsWith("BM"u8)) Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(2)), Is.InRange(file.Length - 4, file.Length), $"{path}: {e.Name}");
        else if (file.AsSpan().StartsWith("RIFF"u8)) Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)) + 8, Is.EqualTo(file.Length), $"{path}: {e.Name}");
        else continue;
        ++checkedFiles;
        if (QuadrupleDArchive.IsCompressed(data)) ++compressedFiles;
      }
    }
    Assert.That(checkedFiles, Is.GreaterThan(0));
    TestContext.Out.WriteLine($"{checkedFiles} files checked, {compressedFiles} of them byte pair encoded");
  }
}
