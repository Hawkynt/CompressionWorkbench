namespace FileFormat.Zpaq;

/// <summary>Arithmetic decoder of a ZPAQ segment (or the stored-block reader for unmodeled blocks).</summary>
/// <remarks>Converted from libzpaq 7.15 (Matt Mahoney, public domain), class <c>Decoder</c>.</remarks>
internal sealed class ZpaqDecoder : IZpaqReader {
  private const int BufferSize = 1 << 16;
  private readonly byte[] _buf = new byte[BufferSize];
  private readonly ZpaqPredictor _pr;
  private uint _low = 1, _high = 0xFFFFFFFF, _curr;
  private int _rpos, _wpos;

  /// <summary>Compressed input.</summary>
  public IZpaqReader? In;

  /// <summary>Creates a decoder for the block held by <paramref name="z"/>.</summary>
  public ZpaqDecoder(ZpaqlMachine z) => this._pr = new(z);

  /// <summary>Bytes read ahead from <see cref="In"/> but not consumed yet.</summary>
  public int Buffered => this._wpos - this._rpos;

  /// <summary>Discards the read-ahead (after the caller repositioned <see cref="In"/>).</summary>
  public void DiscardBuffer() => this._rpos = this._wpos = 0;

  /// <inheritdoc/>
  public int Get() {
    if (this._rpos == this._wpos) {
      this._rpos = 0;
      this._wpos = this.In?.Read(this._buf) ?? 0;
    }
    return this._rpos < this._wpos ? this._buf[this._rpos++] : -1;
  }

  /// <summary>Initialises the model at the start of a block.</summary>
  public void Init() {
    this._pr.Init();
    if (this._pr.IsModeled) {
      this._low = 1;
      this._high = 0xFFFFFFFF;
      this._curr = 0;
    } else
      this._low = this._high = this._curr = 0;
  }

  /// <summary>Returns the next decoded byte, or -1 at the end of the segment.</summary>
  public int Decompress() {
    if (this._pr.IsModeled) {
      if (this._curr == 0)
        for (var i = 0; i < 4; ++i)
          this._curr = this._curr << 8 | (uint)this.Get();
      if (this.Decode(0) != 0) {
        if (this._curr != 0)
          ZpaqError.Throw("decoding end of stream");
        return -1;
      }
      var c = 1;
      while (c < 256) {
        var p = this._pr.Predict() * 2 + 1;
        c += c + this.Decode(p);
        this._pr.Update(c & 1);
      }
      return c - 256;
    }

    if (this._curr == 0) {
      for (var i = 0; i < 4; ++i)
        this._curr = this._curr << 8 | (uint)this.Get();
      if (this._curr == 0)
        return -1;
    }
    --this._curr;
    return this.Get();
  }

  /// <summary>Skips to the end of the compressed segment and returns the byte after it.</summary>
  public int Skip() {
    var c = -1;
    if (this._pr.IsModeled) {
      while (this._curr == 0)
        this._curr = (uint)this.Get();
      while (this._curr != 0 && (c = this.Get()) >= 0)
        this._curr = this._curr << 8 | (uint)c;
      while ((c = this.Get()) == 0) { }
      return c;
    }
    if (this._curr == 0)
      for (var i = 0; i < 4 && (c = this.Get()) >= 0; ++i)
        this._curr = this._curr << 8 | (uint)c;
    while (this._curr > 0) {
      while (this._curr > 0) {
        --this._curr;
        if (this.Get() < 0)
          ZpaqError.Throw("skipped to EOF");
      }
      for (var i = 0; i < 4 && (c = this.Get()) >= 0; ++i)
        this._curr = this._curr << 8 | (uint)c;
    }
    if (c >= 0)
      c = this.Get();
    return c;
  }

  private int Decode(int p) {
    if (this._curr < this._low || this._curr > this._high)
      ZpaqError.Throw("archive corrupted");
    var mid = this._low + (uint)(((ulong)(this._high - this._low) * (uint)p) >> 16);
    int y;
    if (this._curr <= mid) {
      y = 1;
      this._high = mid;
    } else {
      y = 0;
      this._low = mid + 1;
    }
    while ((this._high ^ this._low) < 0x1000000) {
      this._high = this._high << 8 | 255;
      this._low <<= 8;
      if (this._low == 0)
        this._low = 1;
      var c = this.Get();
      if (c < 0)
        ZpaqError.Throw("unexpected end of file");
      this._curr = this._curr << 8 | (uint)c;
    }
    return y;
  }
}

/// <summary>Arithmetic encoder of a ZPAQ segment (or the stored-block writer for unmodeled blocks).</summary>
/// <remarks>Converted from libzpaq 7.15 (Matt Mahoney, public domain), class <c>Encoder</c>.</remarks>
internal sealed class ZpaqEncoder {
  private readonly ZpaqPredictor _pr;
  private uint _low = 1, _high = 0xFFFFFFFF;
  private byte[] _buf = [];

  /// <summary>Compressed output.</summary>
  public IZpaqWriter Out = ZpaqNullWriter.Instance;

  /// <summary>Creates an encoder for the block held by <paramref name="z"/>.</summary>
  public ZpaqEncoder(ZpaqlMachine z) => this._pr = new(z);

  /// <summary>Initialises the model at the start of a block.</summary>
  public void Init() {
    this._low = 1;
    this._high = 0xFFFFFFFF;
    this._pr.Init();
    if (!this._pr.IsModeled) {
      this._low = 0;
      this._buf = new byte[1 << 16];
    }
  }

  /// <summary>Encodes byte <paramref name="c"/> (0..255) or the end of segment (-1).</summary>
  public void Compress(int c) {
    if (this._pr.IsModeled) {
      if (c == -1)
        this.Encode(1, 0);
      else {
        this.Encode(0, 0);
        for (var i = 7; i >= 0; --i) {
          var p = this._pr.Predict() * 2 + 1;
          var y = c >> i & 1;
          this.Encode(y, p);
          this._pr.Update(y);
        }
      }
      return;
    }
    if (this._low != 0 && (c < 0 || this._low == this._buf.Length)) {
      this.Out.Put((int)(this._low >> 24) & 255);
      this.Out.Put((int)(this._low >> 16) & 255);
      this.Out.Put((int)(this._low >> 8) & 255);
      this.Out.Put((int)this._low & 255);
      this.Out.Write(this._buf.AsSpan(0, (int)this._low));
      this._low = 0;
    }
    if (c >= 0)
      this._buf[this._low++] = (byte)c;
  }

  private void Encode(int y, int p) {
    var mid = this._low + (uint)(((ulong)(this._high - this._low) * (uint)p) >> 16);
    if (y != 0)
      this._high = mid;
    else
      this._low = mid + 1;
    while ((this._high ^ this._low) < 0x1000000) {
      this.Out.Put((int)(this._high >> 24));
      this._high = this._high << 8 | 255;
      this._low <<= 8;
      if (this._low == 0)
        this._low = 1;
    }
  }
}

/// <summary>Runs the PCOMP post-processor (if any) over the decoded bytes of a block.</summary>
/// <remarks>Converted from libzpaq 7.15 (Matt Mahoney, public domain), class <c>PostProcessor</c>.</remarks>
internal sealed class ZpaqPostProcessor {
  private int _state;
  private int _hsize;
  private int _ph, _pm;

  /// <summary>The PCOMP machine.</summary>
  public readonly ZpaqlMachine Z = new();

  /// <summary>Current parse state: 0 = initial, 1 = pass-through, 2..4 = loading PCOMP, 5 = running PCOMP.</summary>
  public int State => this._state;

  /// <summary>Resets for a new block with PCOMP array sizes <paramref name="h"/> and <paramref name="m"/>.</summary>
  public void Init(int h, int m) {
    this._state = this._hsize = 0;
    this._ph = h;
    this._pm = m;
    this.Z.Clear();
  }

  /// <summary>Feeds one decoded byte (or -1 at end of segment) and returns the new state.</summary>
  public int Write(int c) {
    switch (this._state) {
      case 0:
        if (c < 0)
          ZpaqError.Throw("unexpected EOS");
        this._state = c + 1;
        if (this._state > 2)
          ZpaqError.Throw("unknown post processing type");
        if (this._state == 1)
          this.Z.Clear();
        break;
      case 1:
        this.Z.OutC(c);
        break;
      case 2:
        if (c < 0)
          ZpaqError.Throw("unexpected EOS");
        this._hsize = c;
        this._state = 3;
        break;
      case 3:
        if (c < 0)
          ZpaqError.Throw("unexpected EOS");
        this._hsize += c * 256;
        if (this._hsize < 1)
          ZpaqError.Throw("empty PCOMP");
        this.Z.Header = new byte[this._hsize + 300];
        this.Z.Cend = 8;
        this.Z.Hbegin = this.Z.Hend = this.Z.Cend + 128;
        this.Z.Header[4] = (byte)this._ph;
        this.Z.Header[5] = (byte)this._pm;
        this._state = 4;
        break;
      case 4:
        if (c < 0)
          ZpaqError.Throw("unexpected EOS");
        this.Z.Header[this.Z.Hend++] = (byte)c;
        if (this.Z.Hend - this.Z.Hbegin == this._hsize) {
          this._hsize = this.Z.Cend - 2 + this.Z.Hend - this.Z.Hbegin;
          this.Z.Header[0] = (byte)this._hsize;
          this.Z.Header[1] = (byte)(this._hsize >> 8);
          this.Z.InitPcomp();
          this._state = 5;
        }
        break;
      case 5:
        this.Z.Run((uint)c);
        if (c < 0)
          this.Z.Flush();
        break;
    }
    return this._state;
  }
}
