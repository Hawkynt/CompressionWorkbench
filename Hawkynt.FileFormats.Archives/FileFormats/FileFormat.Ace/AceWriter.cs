using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Ace;
using CoreAce = Compression.Core.Dictionary.Ace.AceConstants;

namespace FileFormat.Ace;

/// <summary>
/// Writes single-volume ACE archives in the ACE 1.0 layout: stored or ACE 1.0 LZ77 members, optionally
/// solid and/or Blowfish-encrypted, with directory entries. No comments, adverts or recovery records.
/// </summary>
public sealed class AceWriter {
  private readonly int _dictBits;
  private readonly string? _password;
  private readonly bool _solid;
  private readonly bool _store;
  private readonly List<(string Name, byte[] Data, DateTime Modified, bool IsDirectory)> _members = [];

  public AceWriter(int dictionaryBits = CoreAce.DefaultDictBits, string? password = null, bool solid = false, bool store = false) {
    this._dictBits = Math.Clamp(dictionaryBits, CoreAce.MinDictBits, CoreAce.MaxDictBits);
    this._password = string.IsNullOrEmpty(password) ? null : password;
    this._solid = solid;
    this._store = store;
  }

  /// <summary>Archive creation time recorded in the main header (defaults to now).</summary>
  public DateTime? CreatedTime { get; init; }

  public void AddFile(string name, byte[] data, DateTime? modified = null)
    => this._members.Add((name, data, modified ?? DateTime.Now, false));

  public void AddDirectory(string name, DateTime? modified = null)
    => this._members.Add((name.TrimEnd('/', '\\'), [], modified ?? DateTime.Now, true));

  public byte[] ToArray() {
    using var ms = new MemoryStream();
    this.WriteTo(ms);
    return ms.ToArray();
  }

  private static uint ToDos(DateTime t) {
    if (t.Year < 1980) t = new DateTime(1980, 1, 1);
    if (t.Year > 2107) t = new DateTime(2107, 12, 31, 23, 59, 58);
    return (uint)(((t.Year - 1980) << 25) | (t.Month << 21) | (t.Day << 16) | (t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
  }

  private static void WriteHeader(Stream output, ReadOnlySpan<byte> body) {
    Span<byte> head = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(head, AceConstants.AceCrc16(body));
    BinaryPrimitives.WriteUInt16LittleEndian(head[2..], checked((ushort)body.Length));
    output.Write(head);
    output.Write(body);
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var main = new MemoryStream();
    main.WriteByte(AceConstants.HeaderTypeMain);
    Span<byte> u16 = stackalloc byte[2];
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(u16, this._solid ? AceConstants.FlagSolid : (ushort)0);
    main.Write(u16);
    main.Write(Encoding.ASCII.GetBytes(AceConstants.Magic));
    main.WriteByte(10);                        // version needed to extract: ACE 1.0
    main.WriteByte(20);                        // version that created it
    main.WriteByte(AceConstants.HostWin32);
    main.WriteByte(0);                         // volume number
    BinaryPrimitives.WriteUInt32LittleEndian(u32, ToDos(this.CreatedTime ?? DateTime.Now));
    main.Write(u32);
    main.Write(new byte[8]);                   // reserved
    main.WriteByte(0);                         // advert length (always present)
    WriteHeader(output, main.ToArray());

    var compressor = new AceCompressor(this._dictBits, this._solid);
    var first = true;
    foreach (var (name, data, modified, isDirectory) in this._members) {
      byte[] packed;
      byte compType, quality;
      if (isDirectory || data.Length == 0) {
        packed = [];
        compType = CoreAce.CompStore;
        quality = 0;
      } else if (this._store) {
        packed = data;
        compType = CoreAce.CompStore;
        quality = 0;
        compressor.RegisterStored(data);
      } else {
        packed = compressor.Compress(data);
        compType = CoreAce.CompAce10;
        quality = 3;
      }

      var flags = AceConstants.FlagAddSize;
      if (this._solid && !first)
        flags |= AceConstants.FileFlagSolid;
      if (this._password != null && packed.Length > 0) {
        var padded = new byte[(packed.Length + 7) & ~7];
        packed.CopyTo(padded, 0);
        new AceBlowfish(this._password).Encrypt(padded);
        packed = padded;
        flags |= AceConstants.FileFlagPassword;
      }

      var nameBytes = AceConstants.Oem.GetBytes(name.Replace('/', '\\'));
      var body = new MemoryStream();
      body.WriteByte(AceConstants.HeaderTypeFile32);
      BinaryPrimitives.WriteUInt16LittleEndian(u16, flags);
      body.Write(u16);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, checked((uint)packed.Length)); body.Write(u32);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, checked((uint)data.Length)); body.Write(u32);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, ToDos(modified)); body.Write(u32);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, isDirectory ? AceConstants.AttributeDirectory : AceConstants.AttributeArchive); body.Write(u32);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, AceConstants.AceCrc32(data)); body.Write(u32);
      body.WriteByte(compType);
      body.WriteByte(quality);
      BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)(this._dictBits - CoreAce.MinDictBits)); body.Write(u16);
      BinaryPrimitives.WriteUInt16LittleEndian(u16, 0); body.Write(u16); // reserved
      BinaryPrimitives.WriteUInt16LittleEndian(u16, checked((ushort)nameBytes.Length)); body.Write(u16);
      body.Write(nameBytes);
      WriteHeader(output, body.ToArray());
      output.Write(packed);
      first = false;
    }
  }
}
