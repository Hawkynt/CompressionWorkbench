#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Vdfs;

public sealed class VdfsWriter {
  private readonly List<(string Name, FilePayload Payload)> _files = [];

  public void AddFile(string name, byte[] data) => _files.Add((name, FilePayload.FromBytes(data)));

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the descriptor table is
  /// laid out from it before a byte is read.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream)
    => _files.Add((name, FilePayload.FromStream(size, openStream)));

  /// <summary>Materialises the whole descriptor container.</summary>
  public byte[] Build() {
    var header = BuildHeader(out var payloads, out var totalSize);
    if (totalSize > Array.MaxLength)
      throw new InvalidOperationException(
        $"VDFS: a {totalSize:N0}-byte container exceeds the array limit; write it to a seekable stream instead.");
    var full = new byte[totalSize];
    header.CopyTo(full, 0);
    using var target = new MemoryStream(full, writable: true);
    payloads.FlushTo(target);
    return full;
  }

  /// <summary>
  /// Writes the container into <paramref name="output" />: the header and entry
  /// table, then each file's bytes at its recorded offset. Only the header is
  /// ever resident, so a container past what a byte[] can address is producible.
  /// </summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) {
      var full = this.Build();
      output.Write(full, 0, full.Length);
      return;
    }

    var basePosition = output.Position;
    var header = BuildHeader(out var payloads, out var totalSize);
    output.Write(header, 0, header.Length);
    output.SetLength(basePosition + totalSize);
    payloads.FlushTo(output, basePosition);
    output.Position = basePosition + totalSize;
    output.Flush();
  }

  private byte[] BuildHeader(out DeferredPayloads payloads, out long totalSize) {
    var headerSize = 16;
    var fieldsSize = 20;
    var entrySize = 80;
    var entriesStart = headerSize + fieldsSize;
    var dataStart = entriesStart + _files.Count * entrySize;

    // Calculate total size
    var totalDataSize = 0L;
    foreach (var (_, payload) in _files)
      totalDataSize += payload.Size;

    totalSize = dataStart + totalDataSize;
    // Only the header and entry table are materialised; every payload sits in
    // the data area past them and is placed by seek.
    payloads = new DeferredPayloads();
    var result = new byte[dataStart];

    // Header
    "PSVDSC_V2.00\n\r\n\r"u8.CopyTo(result);

    // Fields
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)_files.Count); // entry count
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), (uint)_files.Count); // file count
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), 0); // timestamp
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28), (uint)totalDataSize); // data size
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32), (uint)entriesStart); // root offset

    var currentDataOffset = (long)dataStart;
    for (int i = 0; i < _files.Count; i++) {
      var (name, payload) = _files[i];
      var entryOff = entriesStart + i * entrySize;

      // Name (64 bytes, space-padded)
      var nameBytes = Encoding.ASCII.GetBytes(name);
      Array.Fill(result, (byte)0x20, entryOff, 64);
      Array.Copy(nameBytes, 0, result, entryOff, Math.Min(nameBytes.Length, 64));
      result[entryOff + Math.Min(nameBytes.Length, 63)] = 0; // null terminate

      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(entryOff + 64), (uint)currentDataOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(entryOff + 68), (uint)payload.Size);
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(entryOff + 72), 0x02); // type = file

      payloads.Add(currentDataOffset, payload);
      currentDataOffset += payload.Size;
    }

    return result;
  }
}
