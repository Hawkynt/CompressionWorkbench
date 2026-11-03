namespace FileFormat.Zpaq;

/// <summary>
/// Compiles a ZPAQL configuration (<c>comp ... hcomp ... [pcomp ... ;] ... end</c>) into the
/// COMP/HCOMP header of one machine and the PCOMP code of another.
/// </summary>
/// <remarks>Converted from libzpaq 7.15 (Matt Mahoney, public domain), class <c>Compiler</c>.</remarks>
internal sealed class ZpaqCompiler {
  private const int Jt = 39, Jf = 47, Jmp = 63, Lj = 255;
  private const int Post = 256, Pcomp = 257, End = 258, If = 259, IfNot = 260, Else = 261, EndIf = 262, Do = 263,
    While = 264, Until = 265, Forever = 266, IfL = 267, IfNotL = 268, ElseL = 269;

  private static readonly string?[] ComponentNames = ["", "const", "cm", "icm", "match", "avg", "mix2", "mix", "isse", "sse", null];

  private static readonly string?[] OpcodeList = [
    "error", "a++", "a--", "a!", "a=0", "", "", "a=r",
    "b<>a", "b++", "b--", "b!", "b=0", "", "", "b=r",
    "c<>a", "c++", "c--", "c!", "c=0", "", "", "c=r",
    "d<>a", "d++", "d--", "d!", "d=0", "", "", "d=r",
    "*b<>a", "*b++", "*b--", "*b!", "*b=0", "", "", "jt",
    "*c<>a", "*c++", "*c--", "*c!", "*c=0", "", "", "jf",
    "*d<>a", "*d++", "*d--", "*d!", "*d=0", "", "", "r=a",
    "halt", "out", "", "hash", "hashd", "", "", "jmp",
    "a=a", "a=b", "a=c", "a=d", "a=*b", "a=*c", "a=*d", "a=",
    "b=a", "b=b", "b=c", "b=d", "b=*b", "b=*c", "b=*d", "b=",
    "c=a", "c=b", "c=c", "c=d", "c=*b", "c=*c", "c=*d", "c=",
    "d=a", "d=b", "d=c", "d=d", "d=*b", "d=*c", "d=*d", "d=",
    "*b=a", "*b=b", "*b=c", "*b=d", "*b=*b", "*b=*c", "*b=*d", "*b=",
    "*c=a", "*c=b", "*c=c", "*c=d", "*c=*b", "*c=*c", "*c=*d", "*c=",
    "*d=a", "*d=b", "*d=c", "*d=d", "*d=*b", "*d=*c", "*d=*d", "*d=",
    "", "", "", "", "", "", "", "",
    "a+=a", "a+=b", "a+=c", "a+=d", "a+=*b", "a+=*c", "a+=*d", "a+=",
    "a-=a", "a-=b", "a-=c", "a-=d", "a-=*b", "a-=*c", "a-=*d", "a-=",
    "a*=a", "a*=b", "a*=c", "a*=d", "a*=*b", "a*=*c", "a*=*d", "a*=",
    "a/=a", "a/=b", "a/=c", "a/=d", "a/=*b", "a/=*c", "a/=*d", "a/=",
    "a%=a", "a%=b", "a%=c", "a%=d", "a%=*b", "a%=*c", "a%=*d", "a%=",
    "a&=a", "a&=b", "a&=c", "a&=d", "a&=*b", "a&=*c", "a&=*d", "a&=",
    "a&~a", "a&~b", "a&~c", "a&~d", "a&~*b", "a&~*c", "a&~*d", "a&~",
    "a|=a", "a|=b", "a|=c", "a|=d", "a|=*b", "a|=*c", "a|=*d", "a|=",
    "a^=a", "a^=b", "a^=c", "a^=d", "a^=*b", "a^=*c", "a^=*d", "a^=",
    "a<<=a", "a<<=b", "a<<=c", "a<<=d", "a<<=*b", "a<<=*c", "a<<=*d", "a<<=",
    "a>>=a", "a>>=b", "a>>=c", "a>>=d", "a>>=*b", "a>>=*c", "a>>=*d", "a>>=",
    "a==a", "a==b", "a==c", "a==d", "a==*b", "a==*c", "a==*d", "a==",
    "a<a", "a<b", "a<c", "a<d", "a<*b", "a<*c", "a<*d", "a<",
    "a>a", "a>b", "a>c", "a>d", "a>*b", "a>*c", "a>*d", "a>",
    "", "", "", "", "", "", "", "",
    "", "", "", "", "", "", "", "lj",
    "post", "pcomp", "end", "if", "ifnot", "else", "endif", "do",
    "while", "until", "forever", "ifl", "ifnotl", "elsel", ";", null,
  ];

  private readonly string _in;
  private int _pos;
  private readonly int[] _args;
  private int _line = 1;
  private int _state;
  private readonly Stack<int> _ifStack = new();
  private readonly Stack<int> _doStack = new();

  /// <summary>The text between <c>pcomp</c> and <c>;</c>: the preprocessor command the configuration names.</summary>
  public string PcompCommand { get; private set; } = "";

  /// <summary>
  /// Compiles <paramref name="config"/>, substituting <c>$1..$9</c> from <paramref name="args"/>,
  /// into <paramref name="hz"/> (COMP + HCOMP) and <paramref name="pz"/> (PCOMP).
  /// </summary>
  public ZpaqCompiler(string config, int[]? args, ZpaqlMachine hz, ZpaqlMachine pz) {
    this._in = config;
    this._args = args ?? new int[9];
    hz.Clear();
    pz.Clear();
    hz.Header = new byte[68000];

    this.ReadToken("comp");
    hz.Header[2] = (byte)this.ReadNumber(0, 255);
    hz.Header[3] = (byte)this.ReadNumber(0, 255);
    hz.Header[4] = (byte)this.ReadNumber(0, 255);
    hz.Header[5] = (byte)this.ReadNumber(0, 255);
    var n = this.ReadNumber(0, 255);
    hz.Header[6] = (byte)n;
    hz.Cend = 7;
    for (var i = 0; i < n; ++i) {
      this.ReadNumber(i, i);
      var type = this.ReadToken(ComponentNames);
      hz.Header[hz.Cend++] = (byte)type;
      var clen = type < ZpaqlMachine.ComponentSize.Length ? ZpaqlMachine.ComponentSize[type] : 0;
      if (clen is < 1 or > 10)
        this.SyntaxError("invalid component");
      for (var j = 1; j < clen; ++j)
        hz.Header[hz.Cend++] = (byte)this.ReadNumber(0, 255);
    }
    ++hz.Cend;
    hz.Hbegin = hz.Hend = hz.Cend + 128;

    this.ReadToken("hcomp");
    var op = this.CompileComp(hz);

    var hsize = hz.Cend - 2 + hz.Hend - hz.Hbegin;
    hz.Header[0] = (byte)hsize;
    hz.Header[1] = (byte)(hsize >> 8);

    if (op == Post) {
      this.ReadNumber(0, 0);
      this.ReadToken("end");
    } else if (op == Pcomp) {
      pz.Header = new byte[68000];
      pz.Header[4] = hz.Header[4];
      pz.Header[5] = hz.Header[5];
      pz.Cend = 8;
      pz.Hbegin = pz.Hend = pz.Cend + 128;

      this.Next();
      var start = this._pos;
      while (this._pos < this._in.Length && this._in[this._pos] != ';')
        ++this._pos;
      this.PcompCommand = this._in[start..this._pos];
      if (this._pos < this._in.Length)
        ++this._pos;

      op = this.CompileComp(pz);
      var len = pz.Cend - 2 + pz.Hend - pz.Hbegin;
      pz.Header[0] = (byte)len;
      pz.Header[1] = (byte)(len >> 8);
      if (op != End)
        this.SyntaxError("expected END");
    } else if (op != End)
      this.SyntaxError("expected END or POST 0 END or PCOMP cmd ; ... END");
  }

  private char Cur => this._pos < this._in.Length ? this._in[this._pos] : '\0';

  private char At(int offset) => this._pos + offset < this._in.Length ? this._in[this._pos + offset] : '\0';

  private void Next() {
    for (; this._pos < this._in.Length; ++this._pos) {
      var ch = this._in[this._pos];
      if (ch == '\n')
        ++this._line;
      if (ch == '(')
        this._state += 1 + (this._state < 0 ? 1 : 0);
      else if (this._state > 0 && ch == ')')
        --this._state;
      else if (this._state < 0 && ch <= ' ')
        this._state = 0;
      else if (this._state == 0 && ch > ' ') {
        this._state = -1;
        break;
      }
    }
    if (this._pos >= this._in.Length)
      ZpaqError.Throw("unexpected end of config");
  }

  private static char Lower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 'a' - 'A') : c;

  private bool MatchToken(string word) {
    var a = this._pos;
    var w = 0;
    for (; a < this._in.Length && this._in[a] > ' ' && this._in[a] != '(' && w < word.Length; ++a, ++w)
      if (Lower(this._in[a]) != Lower(word[w]))
        return false;
    var next = a < this._in.Length ? this._in[a] : '\0';
    return w == word.Length && (next <= ' ' || next == '(');
  }

  [System.Diagnostics.CodeAnalysis.DoesNotReturn]
  private void SyntaxError(string message, string? expected = null) {
    var token = new string(this._in.AsSpan(this._pos).ToArray().TakeWhile(c => c > ' ').Take(40).ToArray());
    ZpaqError.Throw($"config line {this._line} at {token}: {message}" + (expected != null ? ", expected: " + expected : ""));
  }

  private int ReadToken(string?[] list) {
    this.Next();
    for (var i = 0; list[i] != null; ++i)
      if (this.MatchToken(list[i]!))
        return i;
    this.SyntaxError("unexpected");
    return -1;
  }

  private void ReadToken(string s) {
    this.Next();
    if (!this.MatchToken(s))
      this.SyntaxError("expected", s);
  }

  private int ReadNumber(int low, int high) {
    this.Next();
    var r = 0;
    if (this.Cur == '$' && this.At(1) is >= '1' and <= '9') {
      if (this.At(2) == '+')
        r = Atoi(this._in, this._pos + 3);
      r += this._args[this.At(1) - '1'];
    } else if (this.Cur == '-' || char.IsAsciiDigit(this.Cur))
      r = Atoi(this._in, this._pos);
    else
      this.SyntaxError("expected a number");
    if (r < low)
      this.SyntaxError("number too low");
    if (r > high)
      this.SyntaxError("number too high");
    return r;
  }

  /// <summary>C <c>atoi</c>: optional sign, then decimal digits up to the first non-digit.</summary>
  private static int Atoi(string s, int i) {
    var negative = false;
    if (i < s.Length && (s[i] == '-' || s[i] == '+')) {
      negative = s[i] == '-';
      ++i;
    }
    var r = 0;
    for (; i < s.Length && char.IsAsciiDigit(s[i]); ++i)
      r = r * 10 + s[i] - '0';
    return negative ? -r : r;
  }

  private int CompileComp(ZpaqlMachine z) {
    int op;
    var compBegin = z.Hend;
    var header = z.Header;
    while (true) {
      op = this.ReadToken(OpcodeList);
      if (op is Post or Pcomp or End)
        break;
      var operand = -1;
      var operand2 = -1;
      if (op == If) {
        op = Jf;
        operand = 0;
        this._ifStack.Push(z.Hend + 1);
      } else if (op == IfNot) {
        op = Jt;
        operand = 0;
        this._ifStack.Push(z.Hend + 1);
      } else if (op is IfL or IfNotL) {
        if (op == IfL)
          header[z.Hend++] = Jt;
        if (op == IfNotL)
          header[z.Hend++] = Jf;
        header[z.Hend++] = 3;
        op = Lj;
        operand = operand2 = 0;
        this._ifStack.Push(z.Hend + 1);
      } else if (op is Else or ElseL) {
        if (op == Else) {
          op = Jmp;
          operand = 0;
        }
        if (op == ElseL) {
          op = Lj;
          operand = operand2 = 0;
        }
        var a = this.PopIf();
        if (header[a - 1] != Lj) {
          var j = z.Hend - a + 1 + (op == Lj ? 1 : 0);
          if (j > 127)
            this.SyntaxError("IF too big, try IFL, IFNOTL");
          header[a] = (byte)j;
        } else {
          var j = z.Hend - compBegin + 2 + (op == Lj ? 1 : 0);
          header[a] = (byte)j;
          header[a + 1] = (byte)(j >> 8);
        }
        this._ifStack.Push(z.Hend + 1);
      } else if (op == EndIf) {
        var a = this.PopIf();
        var j = z.Hend - a - 1;
        if (header[a - 1] != Lj) {
          if (j > 127)
            this.SyntaxError("IF too big, try IFL, IFNOTL, ELSEL");
          header[a] = (byte)j;
        } else {
          j = z.Hend - compBegin;
          header[a] = (byte)j;
          header[a + 1] = (byte)(j >> 8);
        }
      } else if (op == Do)
        this._doStack.Push(z.Hend);
      else if (op is While or Until or Forever) {
        if (this._doStack.Count == 0)
          ZpaqError.Throw("unmatched IF or DO");
        var a = this._doStack.Pop();
        var j = a - z.Hend - 2;
        if (j >= -127) {
          op = op switch { While => Jt, Until => Jf, _ => Jmp };
          operand = j & 255;
        } else {
          j = a - compBegin;
          if (op == While) {
            header[z.Hend++] = Jf;
            header[z.Hend++] = 3;
          }
          if (op == Until) {
            header[z.Hend++] = Jt;
            header[z.Hend++] = 3;
          }
          op = Lj;
          operand = j & 255;
          operand2 = j >> 8;
        }
      } else if ((op & 7) == 7) {
        if (op == Lj) {
          operand = this.ReadNumber(0, 65535);
          operand2 = operand >> 8;
          operand &= 255;
        } else if (op is Jt or Jf or Jmp) {
          operand = this.ReadNumber(-128, 127);
          operand &= 255;
        } else
          operand = this.ReadNumber(0, 255);
      }
      if (op is >= 0 and <= 255)
        header[z.Hend++] = (byte)op;
      if (operand >= 0)
        header[z.Hend++] = (byte)operand;
      if (operand2 >= 0)
        header[z.Hend++] = (byte)operand2;
      if (z.Hend >= header.Length - 130 || z.Hend - z.Hbegin + z.Cend - 2 > 65535)
        this.SyntaxError("program too big");
    }
    header[z.Hend++] = 0;
    return op;
  }

  private int PopIf() {
    if (this._ifStack.Count == 0)
      ZpaqError.Throw("unmatched IF or DO");
    return this._ifStack.Pop();
  }
}
