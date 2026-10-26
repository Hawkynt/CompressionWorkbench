using System.Globalization;
using System.Text;

namespace FileFormat.Zpaq;

/// <summary>
/// ZPAQ compression methods: the method-string language (<c>0</c>..<c>5</c>, <c>x</c>...), the
/// ZPAQL configurations it expands to, and single-block compression with the LZ77 / BWT /
/// E8E9 preprocessors.
/// </summary>
/// <remarks>
/// Converted from libzpaq 7.15 (Matt Mahoney, public domain): <c>makeConfig</c>,
/// <c>compressBlock</c> and <c>e8e9</c>. The generated ZPAQL text is carried over verbatim,
/// because it is what ends up in every block header and the reference decoder runs it.
/// </remarks>
internal static class ZpaqMethods {

  /// <summary>Decimal representation of a non-negative number, at least <paramref name="n"/> digits.</summary>
  internal static string Itos(long x, int n = 1) {
    var s = x.ToString(CultureInfo.InvariantCulture);
    return s.Length >= n ? s : new string('0', n - s.Length) + s;
  }

  /// <summary>floor(log2(x)) + 1: the number of significant bits of <paramref name="x"/>.</summary>
  internal static int Lg(int x) => x == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)x);

  /// <summary>Number of set bits of <paramref name="x"/>.</summary>
  internal static int NBits(int x) => System.Numerics.BitOperations.PopCount((uint)x);

  /// <summary>
  /// E8E9 transform: scanning backward, adds the block offset to the 32-bit operand of every
  /// <c>E8/E9 xx xx xx 00/FF</c> (x86 CALL/JMP) so that repeated targets compress better.
  /// </summary>
  internal static void E8E9(Span<byte> buf) {
    for (var i = buf.Length - 5; i >= 0; --i) {
      if ((buf[i] & 254) != 0xe8 || ((buf[i + 4] + 1) & 254) != 0)
        continue;
      var a = (uint)(buf[i + 1] | buf[i + 2] << 8 | buf[i + 3] << 16) + (uint)i;
      buf[i + 1] = (byte)a;
      buf[i + 2] = (byte)(a >> 8);
      buf[i + 3] = (byte)(a >> 16);
    }
  }

  /// <summary>
  /// Expands an explicit method string (<c>0...</c>, <c>x...</c>, <c>s...</c>, <c>i...</c>) into a ZPAQL
  /// configuration, filling <paramref name="args"/> (<c>$1..$9</c>) from its numeric arguments.
  /// </summary>
  internal static string MakeConfig(string method, int[] args) {
    var type = method[0];
    Array.Clear(args, 0, 9);
    var mi = 1;
    for (var i = 0; i < 9 && mi < method.Length && (char.IsAsciiDigit(method[mi]) || method[mi] == ',' || method[mi] == '.');) {
      if (char.IsAsciiDigit(method[mi]))
        args[i] = args[i] * 10 + method[mi] - '0';
      else if (++i < 9)
        args[i] = 0;
      ++mi;
    }
    // "0..." = No compression
    if (type == '0')
      return "comp 0 0 0 0 0 hcomp end\n";

    // Generate the postprocessor
    string hdr = "", pcomp = "";
    var level = args[1] & 3;
    var doe8 = args[1] >= 4 && args[1] <= 7;

    // LZ77+Huffman, with or without E8E9
    if (level == 1) {
      var rb = args[0] > 4 ? args[0] - 4 : 0;
      hdr = "comp 9 16 0 $1+20 ";
      pcomp =
      "pcomp lazy2 3 ;\n" +
      " (r1 = state\n" +
      "  r2 = len - match or literal length\n" +
      "  r3 = m - number of offset bits expected\n" +
      "  r4 = ptr to buf\n" +
      "  r5 = r - low bits of offset\n" +
      "  c = bits - input buffer\n" +
      "  d = n - number of bits in c)\n" +
      "\n" +
      "  a> 255 if\n";
      if (doe8)
        pcomp +=
        "    b=0 d=r 4 do (for b=0..d-1, d = end of buf)\n" +
        "      a=b a==d ifnot\n" +
        "        a+= 4 a<d if\n" +
        "          a=*b a&= 254 a== 232 if (e8 or e9?)\n" +
        "            c=b b++ b++ b++ b++ a=*b a++ a&= 254 a== 0 if (00 or ff)\n" +
        "              b-- a=*b\n" +
        "              b-- a<<= 8 a+=*b\n" +
        "              b-- a<<= 8 a+=*b\n" +
        "              a-=b a++\n" +
        "              *b=a a>>= 8 b++\n" +
        "              *b=a a>>= 8 b++\n" +
        "              *b=a b++\n" +
        "            endif\n" +
        "            b=c\n" +
        "          endif\n" +
        "        endif\n" +
        "        a=*b out b++\n" +
        "      forever\n" +
        "    endif\n" +
        "\n";
      pcomp +=
      "    (reset state)\n" +
      "    a=0 b=0 c=0 d=0 r=a 1 r=a 2 r=a 3 r=a 4\n" +
      "    halt\n" +
      "  endif\n" +
      "\n" +
      "  a<<=d a+=c c=a               (bits+=a<<n)\n" +
      "  a= 8 a+=d d=a                (n+=8)\n" +
      "\n" +
      "  (if state==0 (expect new code))\n" +
      "  a=r 1 a== 0 if (match code mm,mmm)\n" +
      "    a= 1 r=a 2                 (len=1)\n" +
      "    a=c a&= 3 a> 0 if          (if (bits&3))\n" +
      "      a-- a<<= 3 r=a 3           (m=((bits&3)-1)*8)\n" +
      "      a=c a>>= 2 c=a             (bits>>=2)\n" +
      "      b=r 3 a&= 7 a+=b r=a 3     (m+=bits&7)\n" +
      "      a=c a>>= 3 c=a             (bits>>=3)\n" +
      "      a=d a-= 5 d=a              (n-=5)\n" +
      "      a= 1 r=a 1                 (state=1)\n" +
      "    else (literal, discard 00)\n" +
      "      a=c a>>= 2 c=a             (bits>>=2)\n" +
      "      d-- d--                    (n-=2)\n" +
      "      a= 3 r=a 1                 (state=3)\n" +
      "    endif\n" +
      "  endif\n" +
      "\n" +
      "  (while state==1 && n>=3 (expect match length n*4+ll -> r2))\n" +
      "  do a=r 1 a== 1 if a=d a> 2 if\n" +
      "    a=c a&= 1 a== 1 if         (if bits&1)\n" +
      "      a=c a>>= 1 c=a             (bits>>=1)\n" +
      "      b=r 2 a=c a&= 1 a+=b a+=b r=a 2 (len+=len+(bits&1))\n" +
      "      a=c a>>= 1 c=a             (bits>>=1)\n" +
      "      d-- d--                    (n-=2)\n" +
      "    else\n" +
      "      a=c a>>= 1 c=a             (bits>>=1)\n" +
      "      a=r 2 a<<= 2 b=a           (len<<=2)\n" +
      "      a=c a&= 3 a+=b r=a 2       (len+=bits&3)\n" +
      "      a=c a>>= 2 c=a             (bits>>=2)\n" +
      "      d-- d-- d--                (n-=3)\n";
      if (rb != 0)
        pcomp += "      a= 5 r=a 1                 (state=5)\n";
      else
        pcomp += "      a= 2 r=a 1                 (state=2)\n";
      pcomp +=
      "    endif\n" +
      "  forever endif endif\n" +
      "\n";
      if (rb != 0) pcomp +=  // save r in r5
        "  (if state==5 && n>=8) (expect low bits of offset to put in r5)\n" +
        "  a=r 1 a== 5 if a=d a> " + Itos(rb - 1) + " if\n" +
        "    a=c a&= " + Itos((1 << rb) - 1) + " r=a 5            (save r in r5)\n" +
        "    a=c a>>= " + Itos(rb) + " c=a\n" +
        "    a=d a-= " + Itos(rb) + " d=a\n" +
        "    a= 2 r=a 1                   (go to state 2)\n" +
        "  endif endif\n" +
        "\n";
      pcomp +=
      "  (if state==2 && n>=m) (expect m offset bits)\n" +
      "  a=r 1 a== 2 if a=r 3 a>d ifnot\n" +
      "    a=c r=a 6 a=d r=a 7          (save c=bits, d=n in r6,r7)\n" +
      "    b=r 3 a= 1 a<<=b d=a         (d=1<<m)\n" +
      "    a-- a&=c a+=d                (d=offset=bits&((1<<m)-1)|(1<<m))\n";
      if (rb != 0)
        pcomp +=  // insert r into low bits of d
        "    a<<= " + Itos(rb) + " d=r 5 a+=d a-= " + Itos((1 << rb) - 1) + "\n";
      pcomp +=
      "    d=a b=r 4 a=b a-=d c=a       (c=p=(b=ptr)-offset)\n" +
      "\n" +
      "    (while len-- (copy and output match d bytes from *c to *b))\n" +
      "    d=r 2 do a=d a> 0 if d--\n" +
      "      a=*c *b=a c++ b++          (buf[ptr++]-buf[p++])\n";
      if (!doe8) pcomp += " out\n";
      pcomp +=
      "    forever endif\n" +
      "    a=b r=a 4\n" +
      "\n" +
      "    a=r 6 b=r 3 a>>=b c=a        (bits>>=m)\n" +
      "    a=r 7 a-=b d=a               (n-=m)\n" +
      "    a=0 r=a 1                    (state=0)\n" +
      "  endif endif\n" +
      "\n" +
      "  (while state==3 && n>=2 (expect literal length))\n" +
      "  do a=r 1 a== 3 if a=d a> 1 if\n" +
      "    a=c a&= 1 a== 1 if         (if bits&1)\n" +
      "      a=c a>>= 1 c=a              (bits>>=1)\n" +
      "      b=r 2 a&= 1 a+=b a+=b r=a 2 (len+=len+(bits&1))\n" +
      "      a=c a>>= 1 c=a              (bits>>=1)\n" +
      "      d-- d--                     (n-=2)\n" +
      "    else\n" +
      "      a=c a>>= 1 c=a              (bits>>=1)\n" +
      "      d--                         (--n)\n" +
      "      a= 4 r=a 1                  (state=4)\n" +
      "    endif\n" +
      "  forever endif endif\n" +
      "\n" +
      "  (if state==4 && n>=8 (expect len literals))\n" +
      "  a=r 1 a== 4 if a=d a> 7 if\n" +
      "    b=r 4 a=c *b=a\n";
      if (!doe8) pcomp += " out\n";
      pcomp +=
      "    b++ a=b r=a 4                 (buf[ptr++]=bits)\n" +
      "    a=c a>>= 8 c=a                (bits>>=8)\n" +
      "    a=d a-= 8 d=a                 (n-=8)\n" +
      "    a=r 2 a-- r=a 2 a== 0 if      (if --len<1)\n" +
      "      a=0 r=a 1                     (state=0)\n" +
      "    endif\n" +
      "  endif endif\n" +
      "  halt\n" +
      "end\n";
    }

    // Byte aligned LZ77, with or without E8E9
    else if (level == 2) {
      hdr = "comp 9 16 0 $1+20 ";
      pcomp =
      "pcomp lzpre c ;\n" +
      "  (Decode LZ77: d=state, M=output buffer, b=size)\n" +
      "  a> 255 if (at EOF decode e8e9 and output)\n";
      if (doe8)
        pcomp +=
        "    d=b b=0 do (for b=0..d-1, d = end of buf)\n" +
        "      a=b a==d ifnot\n" +
        "        a+= 4 a<d if\n" +
        "          a=*b a&= 254 a== 232 if (e8 or e9?)\n" +
        "            c=b b++ b++ b++ b++ a=*b a++ a&= 254 a== 0 if (00 or ff)\n" +
        "              b-- a=*b\n" +
        "              b-- a<<= 8 a+=*b\n" +
        "              b-- a<<= 8 a+=*b\n" +
        "              a-=b a++\n" +
        "              *b=a a>>= 8 b++\n" +
        "              *b=a a>>= 8 b++\n" +
        "              *b=a b++\n" +
        "            endif\n" +
        "            b=c\n" +
        "          endif\n" +
        "        endif\n" +
        "        a=*b out b++\n" +
        "      forever\n" +
        "    endif\n";
      pcomp +=
      "    b=0 c=0 d=0 a=0 r=a 1 r=a 2 (reset state)\n" +
      "  halt\n" +
      "  endif\n" +
      "\n" +
      "  (in state d==0, expect a new code)\n" +
      "  (put length in r1 and inital part of offset in r2)\n" +
      "  c=a a=d a== 0 if\n" +
      "    a=c a>>= 6 a++ d=a\n" +
      "    a== 1 if (literal?)\n" +
      "      a+=c r=a 1 a=0 r=a 2\n" +
      "    else (3 to 5 byte match)\n" +
      "      d++ a=c a&= 63 a+= $3 r=a 1 a=0 r=a 2\n" +
      "    endif\n" +
      "  else\n" +
      "    a== 1 if (writing literal)\n" +
      "      a=c *b=a b++\n";
      if (!doe8) pcomp += " out\n";
      pcomp +=
      "      a=r 1 a-- a== 0 if d=0 endif r=a 1 (if (--len==0) state=0)\n" +
      "    else\n" +
      "      a> 2 if (reading offset)\n" +
      "        a=r 2 a<<= 8 a|=c r=a 2 d-- (off=off<<8|c, --state)\n" +
      "      else (state==2, write match)\n" +
      "        a=r 2 a<<= 8 a|=c c=a a=b a-=c a-- c=a (c=i-off-1)\n" +
      "        d=r 1 (d=len)\n" +
      "        do (copy and output d=len bytes)\n" +
      "          a=*c *b=a c++ b++\n";
      if (!doe8) pcomp += " out\n";
      pcomp +=
      "        d-- a=d a> 0 while\n" +
      "        (d=state=0. off, len don\'t matter)\n" +
      "      endif\n" +
      "    endif\n" +
      "  endif\n" +
      "  halt\n" +
      "end\n";
    }

    // BWT with or without E8E9
    else if (level == 3) {  // IBWT
      hdr = "comp 9 16 $1+20 $1+20 ";  // 2^$1 = block size in MB
      pcomp =
      "pcomp bwtrle c ;\n" +
      "\n" +
      "  (read BWT, index into M, size in b)\n" +
      "  a> 255 ifnot\n" +
      "    *b=a b++\n" +
      "\n" +
      "  (inverse BWT)\n" +
      "  elsel\n" +
      "\n" +
      "    (index in last 4 bytes, put in c and R1)\n" +
      "    b-- a=*b\n" +
      "    b-- a<<= 8 a+=*b\n" +
      "    b-- a<<= 8 a+=*b\n" +
      "    b-- a<<= 8 a+=*b c=a r=a 1\n" +
      "\n" +
      "    (save size in R2)\n" +
      "    a=b r=a 2\n" +
      "\n" +
      "    (count bytes in H[~1..~255, ~0])\n" +
      "    do\n" +
      "      a=b a> 0 if\n" +
      "        b-- a=*b a++ a&= 255 d=a d! *d++\n" +
      "      forever\n" +
      "    endif\n" +
      "\n" +
      "    (cumulative counts: H[~i=0..255] = count of bytes before i)\n" +
      "    d=0 d! *d= 1 a=0\n" +
      "    do\n" +
      "      a+=*d *d=a d--\n" +
      "    d<>a a! a> 255 a! d<>a until\n" +
      "\n" +
      "    (build first part of linked list in H[0..idx-1])\n" +
      "    b=0 do\n" +
      "      a=c a>b if\n" +
      "        d=*b d! *d++ d=*d d-- *d=b\n" +
      "      b++ forever\n" +
      "    endif\n" +
      "\n" +
      "    (rest of list in H[idx+1..n-1])\n" +
      "    b=c b++ c=r 2 do\n" +
      "      a=c a>b if\n" +
      "        d=*b d! *d++ d=*d d-- *d=b\n" +
      "      b++ forever\n" +
      "    endif\n" +
      "\n";
      if (args[0] <= 4) {  // faster IBWT list traversal limited to 16 MB blocks
        pcomp +=
        "    (copy M to low 8 bits of H to reduce cache misses in next loop)\n" +
        "    b=0 do\n" +
        "      a=c a>b if\n" +
        "        d=b a=*d a<<= 8 a+=*b *d=a\n" +
        "      b++ forever\n" +
        "    endif\n" +
        "\n" +
        "    (traverse list and output or copy to M)\n" +
        "    d=r 1 b=0 do\n" +
        "      a=d a== 0 ifnot\n" +
        "        a=*d a>>= 8 d=a\n";
        if (doe8) pcomp += " *b=*d b++\n";
        else pcomp += " a=*d out\n";
        pcomp +=
        "      forever\n" +
        "    endif\n" +
        "\n";
        if (doe8)  // IBWT+E8E9
          pcomp +=
          "    (e8e9 transform to out)\n" +
          "    d=b b=0 do (for b=0..d-1, d = end of buf)\n" +
          "      a=b a==d ifnot\n" +
          "        a+= 4 a<d if\n" +
          "          a=*b a&= 254 a== 232 if\n" +
          "            c=b b++ b++ b++ b++ a=*b a++ a&= 254 a== 0 if\n" +
          "              b-- a=*b\n" +
          "              b-- a<<= 8 a+=*b\n" +
          "              b-- a<<= 8 a+=*b\n" +
          "              a-=b a++\n" +
          "              *b=a a>>= 8 b++\n" +
          "              *b=a a>>= 8 b++\n" +
          "              *b=a b++\n" +
          "            endif\n" +
          "            b=c\n" +
          "          endif\n" +
          "        endif\n" +
          "        a=*b out b++\n" +
          "      forever\n" +
          "    endif\n";
        pcomp +=
        "  endif\n" +
        "  halt\n" +
        "end\n";
      } else {  // slower IBWT list traversal for all sized blocks
        if (doe8) {  // E8E9 after IBWT
          pcomp +=
          "    (R2 = output size without EOS)\n" +
          "    a=r 2 a-- r=a 2\n" +
          "\n" +
          "    (traverse list (d = IBWT pointer) and output inverse e8e9)\n" +
          "    (C = offset = 0..R2-1)\n" +
          "    (R4 = last 4 bytes shifted in from MSB end)\n" +
          "    (R5 = temp pending output byte)\n" +
          "    c=0 d=r 1 do\n" +
          "      a=d a== 0 ifnot\n" +
          "        d=*d\n" +
          "\n" +
          "        (store byte in R4 and shift out to R5)\n" +
          "        b=d a=*b a<<= 24 b=a\n" +
          "        a=r 4 r=a 5 a>>= 8 a|=b r=a 4\n" +
          "\n" +
          "        (if E8|E9 xx xx xx 00|FF in R4:R5 then subtract c from x)\n" +
          "        a=c a> 3 if\n" +
          "          a=r 5 a&= 254 a== 232 if\n" +
          "            a=r 4 a>>= 24 b=a a++ a&= 254 a< 2 if\n" +
          "              a=r 4 a-=c a+= 4 a<<= 8 a>>= 8 \n" +
          "              b<>a a<<= 24 a+=b r=a 4\n" +
          "            endif\n" +
          "          endif\n" +
          "        endif\n" +
          "\n" +
          "        (output buffered byte)\n" +
          "        a=c a> 3 if a=r 5 out endif c++\n" +
          "\n" +
          "      forever\n" +
          "    endif\n" +
          "\n" +
          "    (output up to 4 pending bytes in R4)\n" +
          "    b=r 4\n" +
          "    a=c a> 3 a=b if out endif a>>= 8 b=a\n" +
          "    a=c a> 2 a=b if out endif a>>= 8 b=a\n" +
          "    a=c a> 1 a=b if out endif a>>= 8 b=a\n" +
          "    a=c a> 0 a=b if out endif\n" +
          "\n" +
          "  endif\n" +
          "  halt\n" +
          "end\n";
        } else {
          pcomp +=
          "    (traverse list and output)\n" +
          "    d=r 1 do\n" +
          "      a=d a== 0 ifnot\n" +
          "        d=*d\n" +
          "        b=d a=*b out\n" +
          "      forever\n" +
          "    endif\n" +
          "  endif\n" +
          "  halt\n" +
          "end\n";
        }
      }
    }

    // E8E9 or no preprocessing
    else if (level == 0) {
      hdr = "comp 9 16 0 0 ";
      if (doe8) { // E8E9?
        pcomp =
        "pcomp e8e9 d ;\n" +
        "  a> 255 if\n" +
        "    a=c a> 4 if\n" +
        "      c= 4\n" +
        "    else\n" +
        "      a! a+= 5 a<<= 3 d=a a=b a>>=d b=a\n" +
        "    endif\n" +
        "    do a=c a> 0 if\n" +
        "      a=b out a>>= 8 b=a c--\n" +
        "    forever endif\n" +
        "  else\n" +
        "    *b=b a<<= 24 d=a a=b a>>= 8 a+=d b=a c++\n" +
        "    a=c a> 4 if\n" +
        "      a=*b out\n" +
        "      a&= 254 a== 232 if\n" +
        "        a=b a>>= 24 a++ a&= 254 a== 0 if\n" +
        "          a=b a>>= 24 a<<= 24 d=a\n" +
        "          a=b a-=c a+= 5\n" +
        "          a<<= 8 a>>= 8 a|=d b=a\n" +
        "        endif\n" +
        "      endif\n" +
        "    endif\n" +
        "  endif\n" +
        "  halt\n" +
        "end\n";
      } else
        pcomp = "end\n";
    } else
      ZpaqError.Throw("Unsupported method");

    // Build context model (comp, hcomp) assuming:
    // H[0..254] = contexts
    // H[255..511] = location of last byte i-255
    // M = last 64K bytes, filling backward
    // C = pointer to most recent byte
    // R1 = level 2 lz77 1+bytes expected until next code, 0=init
    // R2 = level 2 lz77 first byte of code
    var ncomp = 0;  // number of components
    var membits = args[0] + 20;
    var sb = 5;  // bits in last context
    var comp = "";
    string hcomp = "hcomp\n" +
      "c-- *c=a a+= 255 d=a *d=c\n";
    if (level == 2) {  // put level 2 lz77 parse state in R1, R2
      hcomp +=
      "  (decode lz77 into M. Codes:\n" +
      "  00xxxxxx = literal length xxxxxx+1\n" +
      "  xx......, xx > 0 = match with xx offset bytes to follow)\n" +
      "\n" +
      "  a=r 1 a== 0 if (init)\n" +
      "    a= " + Itos(111 + (doe8 ? 57 : 0)) + " (skip post code)\n" +
      "  else a== 1 if  (new code?)\n" +
      "    a=*c r=a 2  (save code in R2)\n" +
      "    a> 63 if a>>= 6 a++ a++  (match)\n" +
      "    else a++ a++ endif  (literal)\n" +
      "  else (read rest of code)\n" +
      "    a--\n" +
      "  endif endif\n" +
      "  r=a 1  (R1 = 1+expected bytes to next code)\n";
    }

    // Generate the context model

    while (mi < method.Length && ncomp < 254) {

      // parse command C[N1[,N2]...] into v = {C, N1, N2...}
      var v = new List<int> { method[mi++] };
      if (mi < method.Length && char.IsAsciiDigit(method[mi])) {
        v.Add(method[mi++] - '0');
        while (mi < method.Length && (char.IsAsciiDigit(method[mi]) || method[mi] == ',' || method[mi] == '.')) {
          if (char.IsAsciiDigit(method[mi]))
            v[^1] = v[^1] * 10 + method[mi++] - '0';
          else {
            v.Add(0);
            ++mi;
          }
        }
      }

      // c: context model
      // N1%1000: 0=ICM 1..256=CM limit N1-1
      // N1/1000: number of times to halve memory
      // N2: 1..255=offset mod N2. 1000..1255=distance to N2-1000
      // N3...: 0..255=byte mask + 256=lz77 state. 1000+=run of N3-1000 zeros.
      if (v[0] == 'c') {
        while (v.Count < 3) v.Add(0);
        comp += Itos(ncomp) + " ";
        sb = 11;  // count context bits
        if (v[2] < 256) sb += Lg(v[2]);
        else sb += 6;
        for (var i = 3; i < v.Count; ++i)
          if (v[i] < 512) sb += NBits(v[i]) * 3 / 4;
        if (sb > membits) sb = membits;
        if (v[1] % 1000 == 0) comp += "icm " + Itos(sb - 6 - v[1] / 1000) + "\n";
        else comp += "cm " + Itos(sb - 2 - v[1] / 1000) + " " + Itos(v[1] % 1000 - 1) + "\n";

        // special contexts
        hcomp += "d= " + Itos(ncomp) + " *d=0\n";
        if (v[2] > 1 && v[2] <= 255) {  // periodic context
          if (Lg(v[2]) != Lg(v[2] - 1))
            hcomp += "a=c a&= " + Itos(v[2] - 1) + " hashd\n";
          else
            hcomp += "a=c a%= " + Itos(v[2]) + " hashd\n";
        } else if (v[2] >= 1000 && v[2] <= 1255)  // distance context
          hcomp += "a= 255 a+= " + Itos(v[2] - 1000) +
                 " d=a a=*d a-=c a> 255 if a= 255 endif d= " +
                 Itos(ncomp) + " hashd\n";

        // Masked context
        for (var i = 3; i < v.Count; ++i) {
          if (i == 3) hcomp += "b=c ";
          if (v[i] == 255)
            hcomp += "a=*b hashd\n";  // ordinary byte
          else if (v[i] > 0 && v[i] < 255)
            hcomp += "a=*b a&= " + Itos(v[i]) + " hashd\n";  // masked byte
          else if (v[i] >= 256 && v[i] < 512) { // lz77 state or masked literal byte
            hcomp +=
            "a=r 1 a> 1 if\n" +  // expect literal or offset
            "  a=r 2 a< 64 if\n" +  // expect literal
            "    a=*b ";
            if (v[i] < 511) hcomp += "a&= " + Itos(v[i] - 256);
            hcomp += " hashd\n" +
            "  else\n" +  // expect match offset byte
            "    a>>= 6 hashd a=r 1 hashd\n" +
            "  endif\n" +
            "else\n" +  // expect new code
            "  a= 255 hashd a=r 2 hashd\n" +
            "endif\n";
          } else if (v[i] >= 1256)  // skip v[i]-1000 bytes
            hcomp += "a= " + Itos(((v[i] - 1000) >> 8) & 255) + " a<<= 8 a+= "
                 + Itos((v[i] - 1000) & 255) +
            " a+=b b=a\n";
          else if (v[i] > 1000)
            hcomp += "a= " + Itos(v[i] - 1000) + " a+=b b=a\n";
          if (v[i] < 512 && i < v.Count - 1)
            hcomp += "b++ ";
        }
        ++ncomp;
      }

      // m,8,24: MIX, size, rate
      // t,8,24: MIX2, size, rate
      // s,8,32,255: SSE, size, start, limit
      if ("mts".Contains((char)v[0]) && ncomp > (v[0] == 't' ? 1 : 0)) {
        if (v.Count <= 1) v.Add(8);
        if (v.Count <= 2) v.Add(24 + (v[0] == 's' ? 8 : 0));
        if (v[0] == 's' && v.Count <= 3) v.Add(255);
        comp += Itos(ncomp);
        sb = 5 + v[1] * 3 / 4;
        if (v[0] == 'm')
          comp += " mix " + Itos(v[1]) + " 0 " + Itos(ncomp) + " " + Itos(v[2]) + " 255\n";
        else if (v[0] == 't')
          comp += " mix2 " + Itos(v[1]) + " " + Itos(ncomp - 1) + " " + Itos(ncomp - 2)
              + " " + Itos(v[2]) + " 255\n";
        else // s
          comp += " sse " + Itos(v[1]) + " " + Itos(ncomp - 1) + " " + Itos(v[2]) + " "
              + Itos(v[3]) + "\n";
        if (v[1] > 8) {
          hcomp += "d= " + Itos(ncomp) + " *d=0 b=c a=0\n";
          for (; v[1] >= 16; v[1] -= 8) {
            hcomp += "a<<= 8 a+=*b";
            if (v[1] > 16) hcomp += " b++";
            hcomp += "\n";
          }
          if (v[1] > 8)
            hcomp += "a<<= 8 a+=*b a>>= " + Itos(16 - v[1]) + "\n";
          hcomp += "a<<= 8 *d=a\n";
        }
        ++ncomp;
      }

      // i: ISSE chain with order increasing by N1,N2...
      if (v[0] == 'i' && ncomp > 0) {
        hcomp += "d= " + Itos(ncomp - 1) + " b=c a=*d d++\n";
        for (var i = 1; i < v.Count && ncomp < 254; ++i) {
          for (int j = 0; j < v[i] % 10; ++j) {
            hcomp += "hash ";
            if (i < v.Count - 1 || j < v[i] % 10 - 1) hcomp += "b++ ";
            sb += 6;
          }
          hcomp += "*d=a";
          if (i < v.Count - 1) hcomp += " d++";
          hcomp += "\n";
          if (sb > membits) sb = membits;
          comp += Itos(ncomp) + " isse " + Itos(sb - 6 - v[i] / 10) + " " + Itos(ncomp - 1) + "\n";
          ++ncomp;
        }
      }

      // a24,0,0: MATCH. N1=hash multiplier. N2,N3=halve buf, table.
      if (v[0] == 'a') {
        if (v.Count <= 1) v.Add(24);
        while (v.Count < 4) v.Add(0);
        comp += Itos(ncomp) + " match " + Itos(membits - v[3] - 2) + " "
            + Itos(membits - v[2]) + "\n";
        hcomp += "d= " + Itos(ncomp) + " a=*d a*= " + Itos(v[1])
             + " a+=*c a++ *d=a\n";
        sb = 5 + (membits - v[2]) * 3 / 4;
        ++ncomp;
      }

      // w1,65,26,223,20,0: ICM-ISSE chain of length N1 with word contexts,
      // where a word is a sequence of c such that c&N4 is in N2..N2+N3-1.
      // Word is hashed by: hash := hash*N5+c+1
      // Decrease memory by 2^-N6.
      if (v[0] == 'w') {
        if (v.Count <= 1) v.Add(1);
        if (v.Count <= 2) v.Add(65);
        if (v.Count <= 3) v.Add(26);
        if (v.Count <= 4) v.Add(223);
        if (v.Count <= 5) v.Add(20);
        if (v.Count <= 6) v.Add(0);
        comp += Itos(ncomp) + " icm " + Itos(membits - 6 - v[6]) + "\n";
        for (int i = 1; i < v[1]; ++i)
          comp += Itos(ncomp + i) + " isse " + Itos(membits - 6 - v[6]) + " "
              + Itos(ncomp + i - 1) + "\n";
        hcomp += "a=*c a&= " + Itos(v[4]) + " a-= " + Itos(v[2]) + " a&= 255 a< "
             + Itos(v[3]) + " if\n";
        for (int i = 0; i < v[1]; ++i) {
          if (i == 0) hcomp += "  d= " + Itos(ncomp);
          else hcomp += "  d++";
          hcomp += " a=*d a*= " + Itos(v[5]) + " a+=*c a++ *d=a\n";
        }
        hcomp += "else\n";
        for (int i = v[1] - 1; i > 0; --i)
          hcomp += "  d= " + Itos(ncomp + i - 1) + " a=*d d++ *d=a\n";
        hcomp += "  d= " + Itos(ncomp) + " *d=0\n" +
             "endif\n";
        ncomp += v[1] - 1;
        sb = membits - v[6];
        ++ncomp;
      }
    }
    return hdr + Itos(ncomp) + "\n" + comp + hcomp + "halt\n" + pcomp;
  }

  /// <summary>
  /// Compresses <paramref name="input"/> into a single block of one segment, choosing the
  /// algorithm from <paramref name="methodString"/> exactly as libzpaq's <c>compressBlock</c>.
  /// </summary>
  /// <param name="input">The block's data; E8E9 may transform it in place.</param>
  /// <param name="output">Receives the block, preceded by the locator tag.</param>
  /// <param name="methodString">
  /// <c>"LB,R,t"</c> (level 0..5, block size, redundancy 0..255, type: 1 = text, 2 = x86) or an
  /// explicit <c>x</c>/<c>0</c> method.
  /// </param>
  /// <param name="filename">Segment filename, or <see langword="null"/>.</param>
  /// <param name="comment">Appended to the decimal size in the segment comment, or <see langword="null"/>.</param>
  /// <param name="storeSha1">Whether the segment trailer carries the SHA-1 of the data.</param>
  internal static void CompressBlock(ZpaqBuffer input, IZpaqWriter output, string methodString, string? filename = null, byte[]? comment = null, bool storeSha1 = true) {
    var method = methodString;
    var n = input.Size;
    var arg0 = Math.Max(Lg(n + 4095) - 20, 0);

    var type = 0u;
    if (char.IsAsciiDigit(method[0])) {
      var commas = 0;
      Span<int> arg = stackalloc int[4];
      arg.Clear();
      for (var i = 1; i < method.Length && commas < 4; ++i) {
        if (method[i] == ',' || method[i] == '.')
          ++commas;
        else if (char.IsAsciiDigit(method[i]))
          arg[commas] = arg[commas] * 10 + method[i] - '0';
      }
      type = commas == 0 ? 512u : (uint)(arg[1] * 4 + arg[2]);
    }

    byte[]? sha1 = storeSha1 ? Compression.Core.Checksums.Sha1.Compute(input.Span) : null;

    if (char.IsAsciiDigit(method[0])) {
      var level = method[0] - '0';
      var doe8 = (int)(type & 2) * 2;
      method = "x" + Itos(arg0);
      var htsz = "," + Itos(19 + arg0 + (arg0 <= 6 ? 1 : 0));
      var sasz = "," + Itos(21 + arg0);

      if (level == 0)
        method = "0" + Itos(arg0) + ",0";
      else if (level == 1) {
        if (type < 40)
          method += ",0";
        else {
          method += "," + Itos(1 + doe8) + ",";
          if (type < 80) method += "4,0,1,15";
          else if (type < 128) method += "4,0,2,16";
          else if (type < 256) method += "4,0,2" + htsz;
          else if (type < 960) method += "5,0,3" + htsz;
          else method += "6,0,3" + htsz;
        }
      } else if (level == 2) {
        if (type < 32)
          method += ",0";
        else {
          method += "," + Itos(1 + doe8) + ",";
          if (type < 64) method += "4,0,3" + htsz;
          else method += "4,0,7" + sasz + ",1";
        }
      } else if (level == 3) {
        if (type < 20)
          method += ",0";
        else if (type < 48)
          method += "," + Itos(1 + doe8) + ",4,0,3" + htsz;
        else if (type >= 640 || (type & 1) != 0)
          method += "," + Itos(3 + doe8) + "ci1";
        else
          method += "," + Itos(2 + doe8) + ",12,0,7" + sasz + ",1c0,0,511i2";
      } else if (level == 4) {
        if (type < 12)
          method += ",0";
        else if (type < 24)
          method += "," + Itos(1 + doe8) + ",4,0,3" + htsz;
        else if (type < 48)
          method += "," + Itos(2 + doe8) + ",5,0,7" + sasz + "1c0,0,511";
        else if (type < 900) {
          method += "," + Itos(doe8) + "ci1,1,1,1,2a";
          if ((type & 1) != 0)
            method += "w";
          method += "m";
        } else
          method += "," + Itos(3 + doe8) + "ci1";
      } else {
        method += "," + Itos(doe8);
        method += (type & 1) != 0 ? "w2c0,1010,255i1" : "w1i1";
        method += "c256ci1,1,1,1,1,1,2a";

        const int nr = 1 << 12;
        var pt = new int[256];
        var r = new int[nr];
        var p = input.Span;
        if (level > 0) {
          for (var i = 0; i < n; ++i) {
            var k = i - pt[p[i]];
            if (k is > 0 and < nr)
              ++r[k];
            pt[p[i]] = i;
          }
        }

        var n1 = n - r[1] - r[2] - r[3];
        for (var i = 0; i < 2; ++i) {
          var period = 0;
          var score = 0.0;
          var t = 0;
          for (var j = 5; j < nr && t < n1; ++j) {
            var s = r[j] / (256.0 + n1 - t);
            if (s > score) {
              score = s;
              period = j;
            }
            t += r[j];
          }
          if (period > 4 && score > 0.1) {
            method += "c0,0," + Itos(999 + period) + ",255i1";
            if (period <= 255)
              method += "c0," + Itos(period) + "i1";
            n1 -= r[period];
            r[period] = 0;
          } else
            break;
        }
        method += "c0,2,0,255i1c0,3,0,0,255i1c0,4,0,0,0,255i1mm16ts19t0";
      }
    }

    var args = new int[9];
    var config = MakeConfig(method, args);
    var co = new ZpaqCompressor(output);
    co.WriteTag();
    co.StartBlock(config, args);
    var cs = Encoding.ASCII.GetBytes(Itos(n));
    if (comment != null)
      cs = [.. cs, (byte)' ', .. comment];
    co.StartSegment(filename, cs);
    if (args[1] is >= 1 and <= 7 && args[1] != 4)
      co.Compress(new ZpaqLzBuffer(input, args));
    else {
      if (args[1] is >= 4 and <= 7)
        E8E9(input.Span);
      co.Compress(input);
    }
    co.EndSegment(sha1);
    co.EndBlock();
  }
}
