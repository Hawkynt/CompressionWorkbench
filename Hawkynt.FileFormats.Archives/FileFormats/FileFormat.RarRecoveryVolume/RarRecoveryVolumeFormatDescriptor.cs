#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;
using Crc32 = Compression.Core.Checksums.Crc32;

namespace FileFormat.RarRecoveryVolume;

/// <summary>What a RAR recovery volume says about itself and the volume set it belongs to.</summary>
public sealed record RarRecoveryVolume(
  int Format, int DataVolumes, int RecoveryVolumes, int VolumeNumber, IReadOnlyList<(long Size, uint Crc)> DataVolumeChecks, byte[] RecoveryData);

/// <summary>
/// WinRAR recovery volume (<c>.rev</c>), read-only: the parity RAR rebuilds missing volumes of a multi-volume
/// archive from. It holds no files, so the view is <c>metadata.ini</c> (volume counts, this volume's number and,
/// for RAR 5.0, the size and CRC32 of every data volume) and the recovery data; header and data CRCs are
/// checked. Layouts as written down in <c>docs/REV-ON-DISK.md</c>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>docs/REV-ON-DISK.md</c> — the layout, derived from UnRAR's behaviour and measured on RAR 7.12 output</description></item>
///   <item><description><c>https://www.rarlab.com/</c> — RAR and UnRAR, the oracles</description></item>
/// </list>
/// </summary>
public sealed class RarRecoveryVolumeFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  private static ReadOnlySpan<byte> Rev5Signature => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x52, 0x65, 0x76];

  public string Id => "RarRecoveryVolume";
  public string DisplayName => "RAR recovery volume";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rev";
  public IReadOnlyList<string> Extensions => [".rev"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new(Rev5Signature.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rar5", "RAR 5.0"), new("rar3", "RAR 3.x")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "RAR recovery volume (RAR 5.0 and RAR 3.x new style): metadata.ini and recovery data, CRCs checked (read-only)";

  public static RarRecoveryVolume Read(ReadOnlySpan<byte> file) => file.StartsWith(Rev5Signature) ? ReadRar5(file) : ReadRar3(file);

  private static RarRecoveryVolume ReadRar5(ReadOnlySpan<byte> file) {
    if (file.Length < 16) throw new InvalidDataException("REV: truncated RAR 5.0 header.");
    var headerCrc = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
    if (headerSize <= 5 || headerSize > 0x100000 || 16 + headerSize > file.Length)
      throw new InvalidDataException($"REV: RAR 5.0 header of {headerSize} bytes.");
    if (Crc32.Compute(file.Slice(12, 4 + (int)headerSize)) != headerCrc)
      throw new InvalidDataException("REV: RAR 5.0 header CRC mismatch.");
    var h = file.Slice(16, (int)headerSize);
    if (h[0] != 1) throw new InvalidDataException($"REV: RAR 5.0 recovery volume version {h[0]}.");
    int data = BinaryPrimitives.ReadUInt16LittleEndian(h[1..]);
    int recovery = BinaryPrimitives.ReadUInt16LittleEndian(h[3..]);
    int number = BinaryPrimitives.ReadUInt16LittleEndian(h[5..]);
    var dataCrc = BinaryPrimitives.ReadUInt32LittleEndian(h[7..]);
    if (number < data || number >= data + recovery)
      throw new InvalidDataException($"REV: volume number {number} outside {data} data + {recovery} recovery volumes.");
    if (11 + 12 * data > h.Length)
      throw new InvalidDataException($"REV: header too short for {data} data volume records.");
    var checks = new List<(long, uint)>(data);
    for (var i = 0; i < data; ++i)
      checks.Add(((long)BinaryPrimitives.ReadUInt64LittleEndian(h[(11 + 12 * i)..]), BinaryPrimitives.ReadUInt32LittleEndian(h[(19 + 12 * i)..])));
    var recoveryData = file[(16 + (int)headerSize)..];
    if (Crc32.Compute(recoveryData) != dataCrc)
      throw new InvalidDataException("REV: RAR 5.0 recovery data CRC mismatch.");
    return new RarRecoveryVolume(5, data, recovery, number, checks, recoveryData.ToArray());
  }

  private static RarRecoveryVolume ReadRar3(ReadOnlySpan<byte> file) {
    if (file.Length < 8) throw new InvalidDataException("REV: too short for a RAR 3.x trailer.");
    var t = file[^7..];
    int data = t[0] + 1, recovery = t[1] + 1, number = t[2] + 1;
    if (Crc32.Compute(file[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(t[3..]))
      throw new InvalidDataException("REV: not a RAR recovery volume (no RAR 5.0 signature, RAR 3.x trailer CRC mismatch).");
    if (data + recovery > 255 || number > recovery)
      throw new InvalidDataException($"REV: RAR 3.x trailer names volume {number} of {recovery} for {data} data volumes.");
    return new RarRecoveryVolume(3, data, recovery, number, [], file[..^7].ToArray());
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => [.. Entries(Read(ReadAll(stream))).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.Length, e.Data.Length, "stored", false, false, null))];

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(Read(ReadAll(stream))))
      if (files == null || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  private static List<(string Name, byte[] Data)> Entries(RarRecoveryVolume v) {
    var ini = new StringBuilder()
      .Append("[rev]\n")
      .Append(CultureInfo.InvariantCulture, $"format=RAR {v.Format}\n")
      .Append(CultureInfo.InvariantCulture, $"data_volumes={v.DataVolumes}\n")
      .Append(CultureInfo.InvariantCulture, $"recovery_volumes={v.RecoveryVolumes}\n")
      .Append(CultureInfo.InvariantCulture, $"volume_number={v.VolumeNumber}\n");
    for (var i = 0; i < v.DataVolumeChecks.Count; ++i)
      ini.Append(CultureInfo.InvariantCulture, $"data_volume_{i + 1}=size {v.DataVolumeChecks[i].Size} crc32 {v.DataVolumeChecks[i].Crc:x8}\n");
    return [("metadata.ini", Encoding.UTF8.GetBytes(ini.ToString())), ("recovery.bin", v.RecoveryData)];
  }

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
