#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Streams;

namespace FileFormat.PartImage;

/// <summary>
/// Reads a partimage image (version 0.6.x layout) sequentially: the 512-byte volume header, the main
/// header, the segments that follow it (local header, bitmap, info header, optional MBR backups) and
/// the used blocks. A <c>CHK</c> record carrying a CRC-32 and the starting block of the current run
/// follows every 64 KiB of blocks, and a tail closes the volume with the byte sum of everything after
/// the volume header. A whole image may be gzip- or bzip2-compressed. Layout in
/// <c>docs/PARTIMAGE-ON-DISK.md</c>.
/// </summary>
/// <remarks>
/// The reading logic is converted from imagebackup's <c>partimage.py</c> (joergmlpts, MIT,
/// https://github.com/joergmlpts/imagebackup at 6e96ba74e39abd4329e12c4cc7534b8d44ee06f8); see
/// THIRD-PARTY-NOTICE.imagebackup.txt.
/// </remarks>
public sealed class PartImageReader {
  public const int VolumeHeaderSize = 512;
  public const int HeaderSize = 16388;
  public const int CheckFrequency = 65536;
  public const int CheckSize = 16;
  public static ReadOnlySpan<byte> VolumeMagic => "PaRtImAgE-VoLuMe"u8;
  private static ReadOnlySpan<byte> MagicBegin => "MAGIC-BEGIN-"u8;

  /// <summary>Everything the headers say about the image.</summary>
  public sealed record Info(
    string Version, uint Volume, ulong Identifier, string FileSystem, string Description, string Device, uint Compression,
    DateTime Saved, ulong PartitionSize, string Hostname, uint MbrCount, ulong BlockSize, ulong UsedBlocks, ulong BlockCount,
    ulong BitmapSize, string Label, byte[] Bitmap) {
    /// <summary>The size of the restored partition: the partition size, or the blocks when they reach further.</summary>
    public ulong ImageSize => Math.Max(this.PartitionSize, this.BlockSize * this.BlockCount);

    public bool IsUsed(ulong block) => block < this.BlockCount && (this.Bitmap[(int)(block >> 3)] & (1 << (int)(block & 7))) != 0;

    public string Render() {
      var sb = new StringBuilder("[PartImage]\n");
      sb.Append(CultureInfo.InvariantCulture, $"version={this.Version}\nvolume={this.Volume}\nidentifier={this.Identifier}\n");
      sb.Append(CultureInfo.InvariantCulture, $"filesystem={this.FileSystem}\ndescription={this.Description}\ndevice={this.Device}\n");
      sb.Append(CultureInfo.InvariantCulture, $"compression={this.Compression}\nsaved={this.Saved:yyyy-MM-dd HH:mm:ss}\nhostname={this.Hostname}\n");
      sb.Append(CultureInfo.InvariantCulture, $"partition_size={this.PartitionSize}\nmbr_backups={this.MbrCount}\nlabel={this.Label}\n");
      sb.Append(CultureInfo.InvariantCulture, $"block_size={this.BlockSize}\nblock_count={this.BlockCount}\nused_blocks={this.UsedBlocks}\nbitmap_size={this.BitmapSize}\n");
      return sb.ToString();
    }
  }

  private readonly Stream _input;
  private readonly byte[] _buffer = new byte[1 << 16];
  private int _start;
  private int _end;
  private ulong _sum;
  private bool _summing;

  private PartImageReader(Stream input) => this._input = input;

  /// <summary>Opens <paramref name="stream"/>, unwrapping a gzip or bzip2 compressed image.</summary>
  public static PartImageReader Open(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    var head = new byte[3];
    var got = stream.ReadAtLeast(head, 3, throwOnEndOfStream: false);
    if (stream.CanSeek) stream.Position = 0;
    else throw new NotSupportedException("partimage: the input must be seekable.");
    if (got >= 2 && head[0] == 0x1F && head[1] == 0x8B)
      return new PartImageReader(new FileFormat.Gzip.GzipStream(stream, CompressionStreamMode.Decompress, leaveOpen: true));
    if (got == 3 && head[0] == 'B' && head[1] == 'Z' && head[2] == 'h')
      return new PartImageReader(new FileFormat.Bzip2.Bzip2Stream(stream, CompressionStreamMode.Decompress, leaveOpen: true));
    return new PartImageReader(stream);
  }

  /// <summary>Reads the headers up to the start of the data blocks.</summary>
  public Info ReadHeaders() {
    var volume = this.Take(VolumeHeaderSize);
    if (!volume.AsSpan(0, 16).SequenceEqual(VolumeMagic)) throw new InvalidDataException("partimage: missing 'PaRtImAgE-VoLuMe' header.");
    var version = CString(volume.AsSpan(32, 64));
    var volumeNumber = BinaryPrimitives.ReadUInt32LittleEndian(volume.AsSpan(96));
    var identifier = BinaryPrimitives.ReadUInt64LittleEndian(volume.AsSpan(100));
    if (volumeNumber != 0) throw new InvalidDataException($"partimage: this is volume {volumeNumber}, not the first volume of an image.");

    this._summing = true;
    var main = Checked(this.Take(HeaderSize), "main");
    var p = 0;
    string Str(int size) { var s = CString(main.AsSpan(p, size)); p += size; return s; }
    var fileSystem = Str(512);
    var description = Str(4096);
    var device = Str(512);
    p += 4095 + 5 * 65; // first path, uname fields
    var compression = BinaryPrimitives.ReadUInt32LittleEndian(main.AsSpan(p));
    p += 8;
    var tm = Enumerable.Range(0, 6).Select(i => (int)BinaryPrimitives.ReadUInt32LittleEndian(main.AsSpan(p + 4 * i))).ToArray();
    p += 44;
    DateTime saved;
    try {
      saved = new DateTime(tm[5] + 1900, tm[4] + 1, tm[3], tm[2], tm[1], tm[0]);
    } catch (ArgumentOutOfRangeException) {
      saved = default;
    }
    var partitionSize = BinaryPrimitives.ReadUInt64LittleEndian(main.AsSpan(p));
    p += 8;
    var hostname = Str(128);
    p += 64; // version
    var mbrCount = BinaryPrimitives.ReadUInt32LittleEndian(main.AsSpan(p));

    byte[]? local = null, bitmap = null;
    while (true) {
      var name = this.NextSegment() ?? throw new InvalidDataException("partimage: the image ends before its data blocks.");
      if (name == "LOCALHEADER") {
        local = Checked(this.Take(HeaderSize), "local");
      } else if (name == "INFO") {
        Checked(this.Take(HeaderSize), "info");
      } else if (name == "BITMAP") {
        if (local is null) throw new InvalidDataException("partimage: the bitmap comes before the local header.");
        var size = BinaryPrimitives.ReadUInt64LittleEndian(local.AsSpan(24));
        if (size > int.MaxValue) throw new InvalidDataException("partimage: the bitmap is too large.");
        bitmap = this.Take((int)size);
      } else if (name == "DATABLOCKS") {
        break;
      }
    }
    if (local is null || bitmap is null) throw new InvalidDataException("partimage: the local header or bitmap is missing.");
    var blockSize = BinaryPrimitives.ReadUInt64LittleEndian(local);
    var blockCount = BinaryPrimitives.ReadUInt64LittleEndian(local.AsSpan(16));
    if (blockSize is 0 or > 1 << 20 || blockSize * blockCount / blockSize != blockCount)
      throw new InvalidDataException("partimage: impossible block geometry.");
    if ((ulong)bitmap.Length * 8 < blockCount) throw new InvalidDataException("partimage: the bitmap does not cover every block.");
    return new Info(version, volumeNumber, identifier, fileSystem, description, device, compression, saved, partitionSize, hostname, mbrCount,
      blockSize, BinaryPrimitives.ReadUInt64LittleEndian(local.AsSpan(8)), blockCount, (ulong)bitmap.Length, CString(local.AsSpan(40, 64)), bitmap);
  }

  /// <summary>
  /// Writes the restored partition to <paramref name="output"/>: used blocks from the image, unused ones as zeros.
  /// Every 64 KiB group is verified against its CHK record before it is written, and the tail's byte sum at the end.
  /// </summary>
  public void CopyPartition(Info info, Stream output) {
    var blockSize = (int)info.BlockSize;
    // A CHK record names the first block of the current run; runs are cut every 256 KiB of blocks.
    var maxRun = Math.Max(1UL, (1UL << 18) / info.BlockSize);
    // The output that waits for the next CHK record: verified block data, and runs of unused blocks between them.
    var pending = new List<(byte[]? Data, ulong Zeros)>();
    var crc = new Crc32();
    var groupBytes = 0;
    var pendingZeros = 0UL;
    var used = 0UL;
    var runStart = 0UL;
    var zero = new byte[blockSize];
    var previousUsed = false;
    for (var block = 0UL; block < info.BlockCount; ++block) {
      if (!info.IsUsed(block)) {
        previousUsed = false;
        ++pendingZeros;
        continue;
      }
      if (!previousUsed || block - runStart >= maxRun) runStart = block;
      previousUsed = true;
      if (pendingZeros > 0) {
        pending.Add((null, pendingZeros));
        pendingZeros = 0;
      }
      var data = this.Take(blockSize);
      pending.Add((data, 0));
      crc.Update(data);
      ++used;
      groupBytes += blockSize;
      if (groupBytes >= CheckFrequency) {
        var check = this.Take(CheckSize);
        if (!check.AsSpan(0, 4).SequenceEqual("CHK\0"u8)) throw new InvalidDataException($"partimage: no CHK record after block {block}.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(check.AsSpan(4)) != crc.Value)
          throw new InvalidDataException($"partimage: CRC mismatch in the blocks before {block}.");
        if (BinaryPrimitives.ReadUInt64LittleEndian(check.AsSpan(8)) != runStart)
          throw new InvalidDataException($"partimage: the CHK record after block {block} names block {BinaryPrimitives.ReadUInt64LittleEndian(check.AsSpan(8))}, not {runStart}.");
        Flush(pending, output, zero);
        crc.Reset();
        groupBytes = 0;
      }
    }
    if (used != info.UsedBlocks) throw new InvalidDataException($"partimage: the bitmap marks {used} blocks, the header {info.UsedBlocks}.");
    var tailMagic = this.Take(16);
    if (!tailMagic.AsSpan().SequenceEqual("MAGIC-BEGIN-TAIL"u8)) throw new InvalidDataException("partimage: missing tail (a split image needs all its volumes joined).");
    var sum = this._sum;
    this._summing = false;
    var tail = this.Take(12);
    if (BinaryPrimitives.ReadUInt64LittleEndian(tail) != sum) throw new InvalidDataException("partimage: the image's byte sum does not match its tail.");
    pending.Add((null, pendingZeros));
    Flush(pending, output, zero);
    for (var rest = info.ImageSize - info.BlockSize * info.BlockCount; rest > 0;) {
      var n = (int)Math.Min(rest, (ulong)blockSize);
      output.Write(zero, 0, n);
      rest -= (ulong)n;
    }
  }

  private static void Flush(List<(byte[]? Data, ulong Zeros)> pending, Stream output, byte[] zero) {
    foreach (var (data, zeros) in pending)
      if (data is not null) output.Write(data);
      else for (var z = 0UL; z < zeros; ++z) output.Write(zero);
    pending.Clear();
  }

  private static byte[] Checked(byte[] header, string kind) {
    var sum = 0;
    foreach (var b in header.AsSpan(0, HeaderSize - 4)) sum += (sbyte)b;
    if (sum != BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(HeaderSize - 4)))
      throw new InvalidDataException($"partimage: {kind} header checksum mismatch.");
    return header;
  }

  /// <summary>Skips to the next <c>MAGIC-BEGIN-</c> marker and returns its name, or null at the end of the input.</summary>
  private string? NextSegment() {
    while (true) {
      this.Fill(MagicBegin.Length + 32);
      var at = this._buffer.AsSpan(this._start, this._end - this._start).IndexOf(MagicBegin);
      if (at < 0) {
        if (this._end - this._start <= MagicBegin.Length) {
          if (!this.Fill(this._buffer.Length / 2)) return null;
          continue;
        }
        this.Skip(this._end - this._start - MagicBegin.Length);
        continue;
      }
      this.Skip(at + MagicBegin.Length);
      this.Fill(64);
      var n = 0;
      while (this._start + n < this._end && this._buffer[this._start + n] is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'A' and <= (byte)'Z')) ++n;
      var name = Encoding.ASCII.GetString(this._buffer, this._start, n);
      this.Skip(n);
      return name;
    }
  }

  /// <summary>Ensures at least <paramref name="count"/> bytes are buffered when the input has them; false when nothing more came.</summary>
  private bool Fill(int count) {
    if (this._end - this._start >= count) return true;
    if (this._start > 0) {
      Buffer.BlockCopy(this._buffer, this._start, this._buffer, 0, this._end - this._start);
      this._end -= this._start;
      this._start = 0;
    }
    var before = this._end;
    while (this._end - this._start < count && this._end < this._buffer.Length) {
      var r = this._input.Read(this._buffer, this._end, this._buffer.Length - this._end);
      if (r <= 0) break;
      this._end += r;
    }
    return this._end > before;
  }

  private void Skip(int count) {
    if (this._summing) foreach (var b in this._buffer.AsSpan(this._start, count)) this._sum += b;
    this._start += count;
  }

  private byte[] Take(int count) {
    var result = new byte[count];
    var done = 0;
    while (done < count) {
      if (this._start == this._end && !this.Fill(1)) throw new InvalidDataException("partimage: the image is truncated.");
      var n = Math.Min(count - done, this._end - this._start);
      this._buffer.AsSpan(this._start, n).CopyTo(result.AsSpan(done));
      this.Skip(n);
      done += n;
    }
    return result;
  }

  private static string CString(ReadOnlySpan<byte> field) {
    var end = field.IndexOf((byte)0);
    return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
  }
}
