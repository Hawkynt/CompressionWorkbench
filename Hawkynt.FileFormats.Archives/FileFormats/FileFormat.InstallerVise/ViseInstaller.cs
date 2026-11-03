using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.InstallerVise;

/// <summary>One fork an Installer VISE archive yields: catalog path, data (0) or resource (1) fork, bytes and dates.</summary>
public sealed record ViseFork(string Path, int Fork, byte[] Data, DateTime? Modified, int Record);

/// <summary>
/// An Installer VISE application (classic Mac OS): the SVCT data fork holds the members and the
/// catalog, the resource fork (or PEF application) the substitution table. Every self-contained
/// file record is expanded and checked against its catalog CRC-32; directories, actions,
/// base-dependent updates and members on absent media are listed in <see cref="Records"/>.
/// </summary>
/// <remarks>Converted from unvise's <c>extract.c</c> (MIT); see THIRD-PARTY-NOTICE.unvise.txt.</remarks>
public sealed class ViseInstaller {
  private readonly byte[] _data, _payload;
  private readonly uint _catalogOffset;
  private readonly ViseLayout _layout;
  private readonly bool _catalogPacked;
  private readonly List<ViseRecord> _records;
  private readonly byte[]? _data0;
  private byte[]? _table;

  private ViseInstaller(byte[] data, byte[] payload, uint catalogOffset, ViseLayout layout, bool catalogPacked, List<ViseRecord> records, byte[]? data0) {
    this._data = data;
    this._payload = payload;
    this._catalogOffset = catalogOffset;
    this._layout = layout;
    this._catalogPacked = catalogPacked;
    this._records = records;
    this._data0 = data0;
  }

  /// <summary>The catalog revision (low byte of the SVCT format field).</summary>
  public byte Revision { get; private init; }

  /// <summary>Directory paths, in catalog order.</summary>
  public IEnumerable<string> Directories => this._records.Where(r => r.IsDirectory).Select(r => r.Path);

  /// <summary>Every DVCT/FVCT record: path, kind and why a file is not extracted, if it is not.</summary>
  public IEnumerable<(string Path, string Kind)> Records => this._records.Where(r => r.Tag is "DVCT" or "FVCT").Select(r => (r.Path,
    r.IsDirectory ? "directory" : !r.File ? "action" : r.BaseDependent ? "base-dependent update" : r.External ? "external media" : "file"));

  /// <summary>Whether the bytes are an SVCT data fork (raw, or inside MacBinary).</summary>
  public static bool LooksLikeVise(ReadOnlySpan<byte> head)
    => head.StartsWith("SVCT"u8) || (head.Length >= 132 && ViseTransport.IsMacBinary(head) && head[128..].StartsWith("SVCT"u8));

  /// <summary>
  /// Opens an installer from its data fork (or a MacBinary/BinHex file holding both forks), with
  /// the resource fork given separately when it is not inside, and the <c>Installer.data</c>
  /// payload companion when the members live there.
  /// </summary>
  /// <exception cref="InvalidDataException">Not an Installer VISE application, or damaged.</exception>
  /// <exception cref="NotSupportedException">The resource fork is missing, or the layout is not supported.</exception>
  public static ViseInstaller Open(byte[] input, byte[]? resource = null, byte[]? companion = null) {
    ArgumentNullException.ThrowIfNull(input);
    var (data, rsrc) = ViseTransport.Unwrap(input, resource);
    if (data.Length < 4 || !data.AsSpan().StartsWith("SVCT"u8))
      throw new InvalidDataException("VISE: not an Installer VISE application (unpack any StuffIt layer first).");
    if (rsrc == null)
      throw new NotSupportedException("VISE: this is an Installer VISE data fork without its resource fork; give the file as MacBinary or BinHex, or keep its ._name or .rsrc sidecar beside it.");
    if (data.Length < 0x28)
      throw new InvalidDataException("VISE: truncated SVCT data fork.");

    var data0 = ViseTables.LoadData0(rsrc);
    var payload = data;
    if (companion != null) {
      var (inner, _) = ViseTransport.Unwrap(companion, null);
      if (!inner.AsSpan().StartsWith("SVCT"u8))
        payload = inner;
    }

    var revision = data[0x13];
    var segmentCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x14));
    var catalogOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x24));
    if (catalogOffset >= data.Length || catalogOffset + 0x14 > data.Length || !data.AsSpan((int)catalogOffset).StartsWith("CVCT"u8))
      throw new InvalidDataException("VISE: invalid SVCT catalog offset.");
    int recordCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)catalogOffset + 0x10));
    var catalogPacked = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)catalogOffset + 8)) != 0;
    var layout = catalogPacked ? ViseCatalog.PackedLayout(revision) : ViseCatalog.UncompressedLayout(data[0x12], revision);

    var catalogData = data;
    long recordOffset = catalogOffset;
    if (catalogPacked) {
      if (catalogOffset + 0x18 > data.Length || !data.AsSpan((int)catalogOffset + 0x14).StartsWith("PACK"u8))
        throw new InvalidDataException("VISE: packed catalog lacks a PACK header.");
      catalogData = ViseDeflate.InflateCatalog(data, catalogOffset);
      recordOffset = 0;
    }
    var records = ViseCatalog.Read(catalogData, recordOffset, recordCount, layout, revision);
    var installer = new ViseInstaller(data, payload, catalogOffset, layout, catalogPacked, records, data0) { Revision = revision };
    installer.SkipShortPayloadHeaders();

    foreach (var record in records) {
      var packedSize = (long)record.Packed[0] + record.Packed[1];
      var outsideMain = record.Payload > catalogOffset || packedSize > catalogOffset - record.Payload;
      // Early multi-file installers may label a record segment 1 although its payload lies on
      // other media; later layouts deliberately keep update members past the catalog.
      var absentSegment = segmentCount > 1 && (record.Segment != 1 || (outsideMain && layout != ViseLayout.Late));
      if (absentSegment && record.OutputsFork)
        record.External = true;
    }

    if (ReferenceEquals(payload, data) && catalogPacked) {
      // An Active Install stub: only metadata and folder icons here, the members on the web.
      var complete = false;
      var unavailable = false;
      foreach (var record in records) {
        var packedSize = (long)record.Packed[0] + record.Packed[1];
        if (HasPayload(record) && record.Name != "Icon\r")
          complete = true;
        if (record.BaseDependent && packedSize > 0 && (record.Payload >= catalogOffset || packedSize > catalogOffset - record.Payload))
          unavailable = true;
      }
      if (unavailable && !complete)
        throw new NotSupportedException("VISE: the installer downloads its members (Active Install); the stub alone cannot be extracted.");
    }
    return installer;
  }

  private static bool HasPayload(ViseRecord r) => r.OutputsFork && !r.BaseDependent && !r.External;
  private static bool IsEmptyFile(ViseRecord r) => r.IsFileRecord && r.Expanded[0] == 0 && r.Expanded[1] == 0 && r.Packed[0] == 0 && r.Packed[1] == 0;
  private bool SamePayload(ViseRecord r, uint payload) => HasPayload(r) && r.Payload == payload;

  private void SkipShortPayloadHeaders() {
    if (this._layout != ViseLayout.Short)
      return;
    bool? embedded = null;
    foreach (var record in this._records) {
      if (!record.File || (record.Packed[0] == 0 && record.Packed[1] == 0))
        continue;
      var hasHeader = record.Payload + 4L <= this._payload.Length && this._payload.AsSpan((int)record.Payload).StartsWith("FVCT"u8);
      embedded ??= hasHeader;
      if (hasHeader != embedded)
        throw new InvalidDataException("VISE: inconsistent short catalog payload headers.");
      if (embedded != true)
        continue;
      var headerSize = record.End - record.Off;
      if (record.Payload + headerSize > this._payload.Length)
        throw new InvalidDataException("VISE: truncated embedded FVCT payload header.");
      record.Payload += (uint)headerSize;
    }
  }

  private void CheckPayload(long offset, long packedSize) {
    if (!ReferenceEquals(this._payload, this._data)) {
      if (offset + packedSize > this._payload.Length)
        throw new InvalidDataException("VISE: payload outside the companion archive.");
      return;
    }
    if (this._catalogPacked && offset >= this._catalogOffset)
      throw new NotSupportedException("VISE: the installer uses an external payload archive; the stub alone cannot be extracted.");
    if (offset > this._catalogOffset || packedSize > this._catalogOffset - offset)
      throw new InvalidDataException("VISE: payload overlaps the catalog.");
  }

  private byte[] Inflate(long offset, long packedSize, long expected) {
    this.CheckPayload(offset, packedSize);
    this._table ??= ViseTables.Table(this._data, this._data0);
    return ViseDeflate.InflateMember(this._payload.AsSpan((int)offset, (int)packedSize), this._table, expected);
  }

  private static void CheckCrc(ViseRecord record, ReadOnlySpan<byte> data, ReadOnlySpan<byte> resource) {
    var crc = new Crc32();
    if (record.Expanded[0] != 0)
      crc.Update(data[..(int)record.Expanded[0]]);
    if (record.Expanded[1] != 0)
      crc.Update(resource[..(int)record.Expanded[1]]);
    if (crc.Value != record.Checksum)
      throw new InvalidDataException($"VISE: checksum mismatch for '{record.Name ?? "unnamed"}': expected {record.Checksum:X8}, got {crc.Value:X8}.");
  }

  /// <summary>Expands every extractable fork, in catalog order; identical alternatives at one path appear once.</summary>
  public IReadOnlyList<ViseFork> Extract() {
    var outputs = new List<ViseFork>();
    var deferred = new Dictionary<int, (byte[]?[] Forks, bool Emitted)>();
    var sharedDone = new HashSet<uint>();

    void Write(int index, int fork, byte[] bytes) {
      var r = this._records[index];
      outputs.Add(new(r.Path, fork, bytes, MacDate(r.Modified), index));
    }

    void Emit(int index, int fork, ReadOnlySpan<byte> bytes) {
      var record = this._records[index];
      if (record.OutputGroup < 0) {
        Write(index, fork, bytes.ToArray());
        return;
      }
      if (!deferred.TryGetValue(index, out var entry))
        entry = (new byte[]?[2], false);
      entry.Forks[fork] = bytes.ToArray();
      deferred[index] = entry;
      if ((record.Expanded[0] != 0 && entry.Forks[0] == null) || (record.Expanded[1] != 0 && entry.Forks[1] == null))
        return;
      // An alternative byte-identical to one already written at its path is left out.
      for (var i = record.OutputGroup; i < index; ++i)
        if (this._records[i].OutputGroup == record.OutputGroup && deferred.TryGetValue(i, out var earlier) && earlier.Emitted && SameForks(i, index, earlier.Forks, entry.Forks))
          return;
      for (var f = 0; f < 2; ++f)
        if (entry.Forks[f] != null)
          Write(index, f, entry.Forks[f]!);
      deferred[index] = (entry.Forks, true);
    }

    bool SameForks(int a, int b, byte[]?[] fa, byte[]?[] fb) {
      for (var f = 0; f < 2; ++f) {
        if ((this._records[a].Expanded[f] != 0) != (this._records[b].Expanded[f] != 0))
          return false;
        if (this._records[a].Expanded[f] != 0 && !fa[f]!.AsSpan().SequenceEqual(fb[f]))
          return false;
      }
      return true;
    }

    for (var i = 0; i < this._records.Count; ++i) {
      var record = this._records[i];
      if (IsEmptyFile(record)) {
        CheckCrc(record, [], []);
        Emit(i, 0, []);
        continue;
      }
      if (!HasPayload(record))
        continue;
      var shared = this._records.Count(r => this.SamePayload(r, record.Payload));
      switch (this.PayloadLayoutOf(record, shared)) {
        case PayloadKind.Framed: {
            var expanded = this.Inflate(record.Payload, record.Packed[0], record.Packed[1]);
            if (record.ForkOffset[0] > expanded.Length || record.Expanded[0] > expanded.Length - record.ForkOffset[0])
              throw new InvalidDataException("VISE: framed payload is shorter than its data fork.");
            var data = expanded.AsSpan((int)record.ForkOffset[0], (int)record.Expanded[0]);
            CheckCrc(record, data, []);
            Emit(i, 0, data);
            break;
          }
        case PayloadKind.Offset: {
            long total = record.Packed[1];
            for (var f = 0; f < 2; ++f)
              if (record.ForkOffset[f] > total || record.Expanded[f] > total - record.ForkOffset[f])
                throw new InvalidDataException("VISE: offset-fork payload layout overflow.");
            var expanded = this.Inflate(record.Payload, record.Packed[0], total);
            var data = expanded.AsSpan((int)record.ForkOffset[0], (int)record.Expanded[0]);
            var resource = expanded.AsSpan((int)record.ForkOffset[1], (int)record.Expanded[1]);
            CheckCrc(record, data, resource);
            if (record.Expanded[0] != 0)
              Emit(i, 0, data);
            if (record.Expanded[1] != 0)
              Emit(i, 1, resource);
            break;
          }
        case PayloadKind.Shared:
          if (this._records.FindIndex(r => this.SamePayload(r, record.Payload)) != i || !sharedDone.Add(record.Payload))
            continue;
          this.ExtractShared(record, Emit);
          break;
        default: {
            long position = record.Payload;
            var forks = new byte[]?[2];
            for (var f = 0; f < 2; ++f)
              if (record.Packed[f] != 0) {
                forks[f] = this.Inflate(position, record.Packed[f], record.Expanded[f]);
                position += record.Packed[f];
              }
            CheckCrc(record, forks[0], forks[1]);
            for (var f = 0; f < 2; ++f)
              if (forks[f] != null)
                Emit(i, f, forks[f]);
            break;
          }
      }
    }
    return outputs;
  }

  private enum PayloadKind { Separate, Shared, Offset, Framed }

  private PayloadKind PayloadLayoutOf(ViseRecord record, int sharedCount) {
    long memberEnd = 0;
    if (record.HasForkOffsets)
      for (var f = 0; f < 2; ++f)
        memberEnd = Math.Max(memberEnd, (long)record.ForkOffset[f] + record.Expanded[f]);
    if (sharedCount > 1)
      return PayloadKind.Shared;
    if (record.Packed[0] != 0 && record.Packed[1] != 0 && record.Expanded[0] != 0 && record.Expanded[1] == 0
        && record.Packed[1] >= record.Expanded[0] && record.Packed[1] >= memberEnd)
      return PayloadKind.Framed;
    if (record.HasForkOffsets && record.Packed[0] != 0 && memberEnd != 0 && record.Packed[1] >= memberEnd)
      return PayloadKind.Offset;
    return PayloadKind.Separate;
  }

  private delegate void EmitFork(int index, int fork, ReadOnlySpan<byte> bytes);

  private void ExtractShared(ViseRecord record, EmitFork emit) {
    var payload = record.Payload;
    var late = this._layout == ViseLayout.Late;
    if (late && record.Expanded[0] == 0 && record.Packed[0] == 0) {
      this.ExtractSharedResourceEndpoint(payload, emit);
      return;
    }
    var members = this._records.Select((r, i) => (r, i)).Where(x => this.SamePayload(x.r, payload)).ToList();
    long expandedSize = members.Sum(x => (long)x.r.Expanded[0] + x.r.Expanded[1]);
    var hasData = members.Any(x => x.r.Expanded[0] != 0);
    long packedSize;
    // A group with data forks keeps its packed size in field 0 and its whole expanded size in
    // field 1; a resource-only group keeps its packed size in field 1 (the declared sizes give
    // the expansion). Late data-bearing layouts use the first form.
    if (late && hasData) {
      if (record.Packed[0] == 0 || record.Packed[1] == 0)
        throw new InvalidDataException($"VISE: unknown shared payload layout at 0x{payload:X}.");
      expandedSize = record.Packed[1];
      packedSize = record.Packed[0];
    } else if (record.Packed[0] != 0 && record.Packed[1] != 0) {
      expandedSize = record.Packed[1];
      packedSize = record.Packed[0];
    } else if (!hasData && record.Packed[1] != 0)
      packedSize = record.Packed[1];
    else
      throw new InvalidDataException($"VISE: unknown shared payload layout at 0x{payload:X}.");

    var expanded = this.Inflate(payload, packedSize, expandedSize);
    long position = 0, coveredEnd = 0;
    var usesOffsets = false;
    foreach (var (r, i) in members) {
      var forks = new (int Start, int Length)?[2];
      for (var f = 0; f < 2; ++f) {
        if (r.Expanded[f] == 0)
          continue;
        long size = r.Expanded[f];
        if (r.HasForkOffsets) {
          usesOffsets = true;
          position = r.ForkOffset[f];
        }
        if (position > expanded.Length || size > expanded.Length - position)
          throw new InvalidDataException("VISE: shared payload layout overflow.");
        forks[f] = ((int)position, (int)size);
        position += size;
        coveredEnd = Math.Max(coveredEnd, position);
      }
      Span<byte> data = forks[0] is { } d ? expanded.AsSpan(d.Start, d.Length) : [];
      Span<byte> resource = forks[1] is { } rs ? expanded.AsSpan(rs.Start, rs.Length) : [];
      CheckCrc(r, data, resource);
      if (forks[0] != null)
        emit(i, 0, data);
      if (forks[1] != null)
        emit(i, 1, resource);
    }
    if (coveredEnd != expanded.Length && !usesOffsets && !late)
      throw new InvalidDataException("VISE: unassigned bytes in a shared payload.");
  }

  private void ExtractSharedResourceEndpoint(uint payload, EmitFork emit) {
    long packedSize = 0, expandedSize = 0;
    var endpoint = -1;
    for (var i = 0; i < this._records.Count; ++i) {
      var r = this._records[i];
      if (!this.SamePayload(r, payload))
        continue;
      if (r.Expanded[0] != 0 || r.Packed[0] != 0 || r.Expanded[1] == 0 || r.Packed[1] == 0)
        throw new InvalidDataException("VISE: invalid resource-only shared payload.");
      packedSize = Math.Max(packedSize, r.Packed[1]);
      expandedSize = Math.Max(expandedSize, r.Expanded[1]);
      if (r.Packed[1] == packedSize)
        endpoint = i;
    }
    if (endpoint < 0 || this._records[endpoint].Subtype == 6)
      throw new InvalidDataException("VISE: invalid resource-only shared payload endpoint.");
    for (var i = 0; i < this._records.Count; ++i)
      if (this.SamePayload(this._records[i], payload) && i != endpoint && this._records[i].Subtype != 6)
        throw new InvalidDataException("VISE: unknown resource-only shared payload layout.");
    var expanded = this.Inflate(payload, packedSize, expandedSize);
    // Shorter endpoints are installer-private update references into this member, not files.
    var record = this._records[endpoint];
    CheckCrc(record, [], expanded);
    emit(endpoint, 1, expanded.AsSpan(0, (int)record.Expanded[1]));
  }

  private static DateTime? MacDate(uint seconds) => seconds == 0 ? null : new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
}
