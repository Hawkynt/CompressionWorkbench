using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>
/// A ZPL object set's system-attribute tables: attribute numbers and lengths from the
/// <c>REGISTRY</c> ZAP, attribute orders from the <c>LAYOUTS</c> ZAP
/// (<c>docs/ZFS-ON-DISK.md</c>, "System attributes").
/// </summary>
internal sealed class ZfsSystemAttributes {
  private readonly Dictionary<int, (string Name, int Length)> _attributes = new();
  private readonly Dictionary<int, ushort[]> _layouts = new();

  public ZfsSystemAttributes(ZfsObjectSet os, ulong masterObject) {
    foreach (var entry in os.Zap(masterObject, "SA master node")) {
      switch (entry.Name) {
        case "REGISTRY":
          foreach (var attribute in os.Zap(entry.Value, "SA registry"))
            this._attributes[(int)(attribute.Value & 0xFFFF)] = (attribute.Name, (int)((attribute.Value >> 24) & 0xFFFF));
          break;
        case "LAYOUTS":
          foreach (var layout in os.Zap(entry.Value, "SA layouts"))
            if (int.TryParse(layout.Name, out var number))
              this._layouts[number] = layout.Values.Select(v => (ushort)v).ToArray();
          break;
      }
    }
  }

  /// <summary>Adds the attributes held in one SA container (a bonus buffer or a spill block) to <paramref name="into"/>.</summary>
  public void Parse(ReadOnlySpan<byte> container, Dictionary<string, byte[]> into) {
    if (container.Length < 8) throw new InvalidDataException("ZFS: SA header truncated.");
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(container);
    if (magic != ZfsConstants.SaMagic)
      throw new InvalidDataException($"ZFS: SA magic 0x{magic:X} mismatch.");
    var info = BinaryPrimitives.ReadUInt16LittleEndian(container[4..]);
    var layoutNumber = info & 0x3FF;
    var headerSize = ((info >> 10) & 0x3F) * 8;
    if (!this._layouts.TryGetValue(layoutNumber, out var layout))
      throw new InvalidDataException($"ZFS: SA layout {layoutNumber} is not registered.");

    var lengthIndex = 0;
    var at = headerSize;
    foreach (var number in layout) {
      if (!this._attributes.TryGetValue(number, out var attribute))
        throw new InvalidDataException($"ZFS: SA attribute {number} is not registered.");
      var length = attribute.Length;
      if (length == 0) {
        var lengthAt = 6 + 2 * lengthIndex++;
        if (lengthAt + 2 > headerSize)
          throw new InvalidDataException("ZFS: SA header lacks a variable-length entry.");
        length = BinaryPrimitives.ReadUInt16LittleEndian(container[lengthAt..]);
      }
      if (at + length > container.Length)
        throw new InvalidDataException($"ZFS: SA attribute {attribute.Name} runs past its container.");
      into[attribute.Name] = container.Slice(at, length).ToArray();
      at += (length + 7) & ~7;
    }
  }
}
