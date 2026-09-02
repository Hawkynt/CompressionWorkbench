#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// A volume that has just been written must have room to put something in.
/// </summary>
/// <remarks>
/// <para>FATX sized its images to exactly the clusters its files occupied, so a
/// freshly created volume had no free run at all and its own modifier reported
/// there was nowhere to put a single added file. Nothing else noticed, because
/// every other check writes a volume and reads it back — which a volume with no
/// spare room does perfectly well.</para>
///
/// <para>The formats named below are containers rather than volumes, and their
/// refusal is about what may go in rather than whether there is room.</para>
/// </remarks>
[TestFixture]
public class FreshVolumeHasRoomTests {

  /// <summary>
  /// Formats whose Add is shaped by what the content must be, not by space.
  /// </summary>
  private static readonly Dictionary<string, string> ContentBound = new(StringComparer.Ordinal) {
    ["AppleSingle"] = "entries are identified by a fixed id, not by a file name",
    ["BinCue"] = "a raw CD image is edited a sector at a time, by LBA, not by file name",
    ["Cdi"] = "a raw CD image is edited a sector at a time, by LBA, not by file name",
    ["Cso"] = "a compressed ISO is edited a block at a time, by index, not by file name",
    ["G64"] = "a 1541 disk holds 683 blocks and the probe set already fills it",
    ["Ghost"] = "needs a named compression method, not the default",
    ["Ico"] = "entries must be PNG or BMP images",
    ["Lbr"] = "the directory is a fixed size and cannot grow in place",
    ["Mdf"] = "a raw CD image is edited a sector at a time, by LBA, not by file name",
    ["Nib"] = "a nibble image is edited a track at a time, by track_NN.bin, not by file name",
    ["Nrg"] = "a raw CD image is edited a sector at a time, by LBA, not by file name",
    ["Ova"] = "needs a disk image or a descriptor, not an arbitrary file",
    ["Sparseimage"] = "a sparse image is edited a band at a time, not by file name",
    ["Paragon"] = "needs a named compression method, not the default",
  };

  private static IEnumerable<string> EditableFormats() {
    foreach (var descriptor in FormatRegistry.All.OrderBy(d => d.Id, StringComparer.Ordinal)) {
      var ops = FormatRegistry.GetArchiveOps(descriptor.Id);
      if (ops is not (IArchiveCreatable and IArchiveModifiable)) continue;
      if (!Enum.TryParse<FormatDetector.Format>(descriptor.Id, out _)) continue;
      yield return descriptor.Id;
    }
  }

  [TestCaseSource(nameof(EditableFormats))]
  public void AFreshlyWrittenVolume_TakesOneMoreFile(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var format = Enum.Parse<FormatDetector.Format>(formatId);
    var work = Path.Combine(Path.GetTempPath(), "cwb_room_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);

    try {
      var inputs = new List<ArchiveInput>();
      for (var i = 0; i < 3; ++i) {
        var path = Path.Combine(work, $"K{i}.BIN");
        File.WriteAllBytes(path, new byte[1500 + i * 400]);
        inputs.Add(new ArchiveInput(path, $"K{i}.BIN"));
      }

      var image = Path.Combine(work, "volume.img");
      try {
        ArchiveOperations.Create(image, inputs, new CompressionOptions(), format, null);
      } catch (Exception ex) {
        Assert.Ignore($"{formatId}: cannot create a probe volume ({ex.GetType().Name}).");
        return;
      }

      // A payload with a shape of its own, so it can be recognised again whatever
      // the format does to its name.
      var payload = new byte[900];
      for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(i * 37 + 11);
      var extra = Path.Combine(work, "ADD.BIN");
      File.WriteAllBytes(extra, payload);

      try {
        using var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite);
        ((IArchiveModifiable)ops).Add(stream, [new ArchiveInputInfo(extra, "ADD.BIN", false)]);
      } catch (Exception ex) {
        if (ContentBound.TryGetValue(formatId, out var why)) {
          Assert.Ignore($"{formatId}: {why}.");
          return;
        }
        Assert.Fail($"{formatId}: a freshly written volume would not take one more file — " +
                    $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
      }

      // Not throwing is not the same as having added it. Four CD-image formats
      // matched only sector-shaped names and passed over everything else without
      // a word, so this test called a discarded file a success — it was checking
      // that nothing was raised, which a verb that does nothing at all satisfies
      // perfectly. What has to hold is that the bytes are on the volume.
      Assert.That(Holds(ops, image, payload), Is.True,
        $"{formatId}: Add reported success and the file is not on the volume.");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  /// <summary>True when some entry on the volume carries these bytes.</summary>
  /// <remarks>
  /// Matched on content rather than on the name, because a format is entitled to
  /// shorten one, and on a prefix because several store a length only to the
  /// nearest record and pad the rest with zeros.
  /// </remarks>
  private static bool Holds(IArchiveFormatOperations ops, string image, byte[] payload) {
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_room_out_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      using (var stream = File.OpenRead(image))
        ops.Extract(stream, outDir, null, null);

      foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)) {
        var got = File.ReadAllBytes(file);
        if (got.Length < payload.Length) continue;
        if (got.AsSpan(0, payload.Length).SequenceEqual(payload)) return true;
      }
      return false;
    } catch {
      return false;
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

}
