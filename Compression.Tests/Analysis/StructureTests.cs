using System.Buffers.Binary;
using Compression.Analysis.Structure;

namespace Compression.Tests.Analysis;

[TestFixture]
public class StructureTests {

  // ── TemplateParser tests ──────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Parser_SimpleStruct() {
    var source = """
      struct Test {
        u32le magic;
        u16le version;
        u8 flags;
      };
      """;
    var template = TemplateParser.Parse(source);
    Assert.That(template.Structs.Count, Is.EqualTo(1));
    Assert.That(template.Root.Name, Is.EqualTo("Test"));
    Assert.That(template.Root.Fields.Count, Is.EqualTo(3));
    Assert.That(template.Root.Fields[0].Type, Is.EqualTo(FieldType.U32LE));
    Assert.That(template.Root.Fields[1].Type, Is.EqualTo(FieldType.U16LE));
  }

  [Test, Category("HappyPath")]
  public void Parser_ArrayWithFieldRef() {
    var source = """
      struct Header {
        u16le nameLen;
        char[nameLen] name;
      };
      """;
    var template = TemplateParser.Parse(source);
    Assert.That(template.Root.Fields[1].ArrayLength, Is.EqualTo("nameLen"));
    Assert.That(template.Root.Fields[1].Type, Is.EqualTo(FieldType.CharArray));
  }

  [Test, Category("HappyPath")]
  public void Parser_Comments_Ignored() {
    var source = """
      // This is a comment
      struct Foo {
        u8 val; // inline comment
      };
      """;
    var template = TemplateParser.Parse(source);
    Assert.That(template.Root.Fields.Count, Is.EqualTo(1));
  }

  [Test, Category("EdgeCase")]
  public void Parser_InvalidSyntax_Throws() {
    Assert.Throws<FormatException>(() => TemplateParser.Parse("not a struct"));
  }

  [Test, Category("EdgeCase")]
  public void Parser_EmptyTemplate_Throws() {
    Assert.Throws<FormatException>(() => TemplateParser.Parse("// just a comment\n"));
  }

  // ── StructureInterpreter tests ────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Interpreter_U32LE() {
    var source = """
      struct Test {
        u32le value;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(data, 0x12345678);
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields.Count, Is.EqualTo(1));
    Assert.That(fields[0].DisplayValue, Does.Contain("12345678"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_CharArrayWithFieldRef() {
    var source = """
      struct Test {
        u16le len;
        char[len] name;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[7]; // 2 bytes for len + 5 bytes for "Hello"
    BinaryPrimitives.WriteUInt16LittleEndian(data, 5);
    System.Text.Encoding.ASCII.GetBytes("Hello").CopyTo(data.AsSpan(2));
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields.Count, Is.EqualTo(2));
    Assert.That(fields[0].DisplayValue, Does.Contain("5"));
    Assert.That(fields[1].DisplayValue, Does.Contain("Hello"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_RepeatToEof() {
    var source = """
      struct Test {
        u8[*] data;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields.Count, Is.EqualTo(1));
    Assert.That(fields[0].Size, Is.EqualTo(4));
    Assert.That(fields[0].DisplayValue, Does.Contain("DEADBEEF"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_ZipHeader_BuiltIn() {
    var template = TemplateParser.Parse(BuiltInTemplates.ZipLocalHeader, "ZIP");
    // Build a minimal ZIP local header
    var data = new byte[34];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 0x04034B50); // signature
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 20); // version
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 4); // nameLength
    System.Text.Encoding.ASCII.GetBytes("test").CopyTo(data.AsSpan(30));

    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].Name, Is.EqualTo("signature"));
    Assert.That(fields[0].DisplayValue, Does.Contain("04034B50"));
    // Find fileName field
    var nameField = fields.FirstOrDefault(f => f.Name == "fileName");
    Assert.That(nameField, Is.Not.Null);
    Assert.That(nameField!.DisplayValue, Does.Contain("test"));
  }

  [Test, Category("HappyPath")]
  public void BuiltInTemplates_AllParse() {
    foreach (var (name, source) in BuiltInTemplates.All) {
      var template = TemplateParser.Parse(source, name);
      Assert.That(template.Structs.Count, Is.GreaterThanOrEqualTo(1), $"Template '{name}' has no structs");
    }
  }

  // ── Multi-field per line ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Parser_MultipleFieldsPerLine() {
    var source = """
      struct Test {
        u8 r2;u8 r1;u8 r;
      };
      """;
    var template = TemplateParser.Parse(source);
    Assert.That(template.Root.Fields.Count, Is.EqualTo(3));
    Assert.That(template.Root.Fields[0].Name, Is.EqualTo("r2"));
    Assert.That(template.Root.Fields[1].Name, Is.EqualTo("r1"));
    Assert.That(template.Root.Fields[2].Name, Is.EqualTo("r"));
  }

  [Test, Category("HappyPath")]
  public void Parser_MultipleFieldsPerLine_WithSpaces() {
    var source = """
      struct Test {
        u16le a; u32le b; i8 c;
      };
      """;
    var template = TemplateParser.Parse(source);
    Assert.That(template.Root.Fields.Count, Is.EqualTo(3));
    Assert.That(template.Root.Fields[0].Type, Is.EqualTo(FieldType.U16LE));
    Assert.That(template.Root.Fields[1].Type, Is.EqualTo(FieldType.U32LE));
    Assert.That(template.Root.Fields[2].Type, Is.EqualTo(FieldType.I8));
  }

  [Test, Category("HappyPath")]
  public void Parser_MultipleFieldsPerLine_WithTrailingComment() {
    var source = """
      struct Test {
        u8 a;u8 b; // comment after
      };
      """;
    var template = TemplateParser.Parse(source);
    Assert.That(template.Root.Fields.Count, Is.EqualTo(2));
  }

  // ── Bitfields ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Interpreter_Bitfields() {
    var source = """
      struct Test {
        bits[4] lo;
        bits[4] hi;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[] { 0xA5 }; // lo=5 (0101), hi=10 (1010)
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields.Count, Is.EqualTo(2));
    Assert.That(fields[0].DisplayValue, Does.Contain("5"));
    Assert.That(fields[1].DisplayValue, Does.Contain("10"));
  }

  // ── Typed arrays ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Interpreter_TypedArray_U16le() {
    var source = """
      struct Test {
        u16le[3] values;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0), 100);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 200);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 300);
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields.Count, Is.EqualTo(1));
    Assert.That(fields[0].Children, Is.Not.Null);
    Assert.That(fields[0].Children!.Count, Is.EqualTo(3));
    Assert.That(fields[0].DisplayValue, Does.Contain("100"));
    Assert.That(fields[0].DisplayValue, Does.Contain("300"));
  }

  // ── New types: Date/Time ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Interpreter_UnixTimestamp32() {
    var source = """
      struct Test {
        unixts32le ts;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(data, 1700000000); // 2023-11-14
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Does.Contain("2023"));
    Assert.That(fields[0].DisplayValue, Does.Contain("UTC"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_DosDate() {
    var source = """
      struct Test {
        dosdate d;
      };
      """;
    var template = TemplateParser.Parse(source);
    // 2023-06-15: year=43(2023-1980), month=6, day=15
    var raw = (ushort)((43 << 9) | (6 << 5) | 15);
    var data = new byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(data, raw);
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Does.Contain("2023-06-15"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_DosTime() {
    var source = """
      struct Test {
        dostime t;
      };
      """;
    var template = TemplateParser.Parse(source);
    // 14:30:20 → hour=14, min=30, sec=10 (20/2)
    var raw = (ushort)((14 << 11) | (30 << 5) | 10);
    var data = new byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(data, raw);
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Does.Contain("14:30:20"));
  }

  // ── New types: Color ─────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Interpreter_Rgb24() {
    var source = """
      struct Test {
        rgb24 color;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[] { 0xFF, 0x80, 0x00 }; // R=255 G=128 B=0
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Does.Contain("FF8000"));
    Assert.That(fields[0].DisplayValue, Does.Contain("255,128,0"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_Rgb565() {
    var source = """
      struct Test {
        rgb565le pixel;
      };
      """;
    var template = TemplateParser.Parse(source);
    // R=31, G=63, B=31 → 0xFFFF
    var data = new byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(data, 0xFFFF);
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Does.Contain("R=31"));
    Assert.That(fields[0].DisplayValue, Does.Contain("G=63"));
  }

  // ── New types: Network/ID ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Interpreter_Ipv4() {
    var source = """
      struct Test {
        ipv4 addr;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[] { 192, 168, 1, 100 };
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Is.EqualTo("192.168.1.100"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_Mac48() {
    var source = """
      struct Test {
        mac48 addr;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Is.EqualTo("AA:BB:CC:DD:EE:FF"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_FourCC() {
    var source = """
      struct Test {
        fourcc code;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = System.Text.Encoding.ASCII.GetBytes("RIFF");
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Does.Contain("RIFF"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_Guid() {
    var source = """
      struct Test {
        guid id;
      };
      """;
    var template = TemplateParser.Parse(source);
    var guid = new Guid("12345678-1234-1234-1234-123456789abc");
    var data = guid.ToByteArray();
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Does.Contain("12345678-1234-1234-1234-123456789abc"));
  }

  // ── New types: Fixed-point / BCD / Special ───────────────────────────

  [Test, Category("HappyPath")]
  public void Interpreter_Q8_8() {
    var source = """
      struct Test {
        q8_8le val;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[2];
    BinaryPrimitives.WriteInt16LittleEndian(data, 0x0180); // 1.5 in Q8.8 = 384/256 = 1.5
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Does.Contain("1.5"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_Bcd8() {
    var source = """
      struct Test {
        bcd8 val;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[] { 0x42 }; // BCD for 42
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Is.EqualTo("42"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_Bool8() {
    var source = """
      struct Test {
        bool8 a;
        bool8 b;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[] { 0x00, 0x01 };
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Does.Contain("false"));
    Assert.That(fields[1].DisplayValue, Does.Contain("true"));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_F16LE() {
    var source = """
      struct Test {
        f16le val;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[2];
    BinaryPrimitives.WriteHalfLittleEndian(data, (Half)3.14);
    var fields = StructureInterpreter.Interpret(template, data);
    Assert.That(fields[0].DisplayValue, Does.Contain("3.14"));
  }

  // ── Type aliases ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Parser_TypeAliases() {
    var source = """
      struct Test {
        byte a;
        sbyte b;
        uint32le c;
        int16be d;
        floatle e;
        doublebe f;
        bool g;
      };
      """;
    var template = TemplateParser.Parse(source);
    Assert.That(template.Root.Fields[0].Type, Is.EqualTo(FieldType.U8));
    Assert.That(template.Root.Fields[1].Type, Is.EqualTo(FieldType.I8));
    Assert.That(template.Root.Fields[2].Type, Is.EqualTo(FieldType.U32LE));
    Assert.That(template.Root.Fields[3].Type, Is.EqualTo(FieldType.I16BE));
    Assert.That(template.Root.Fields[4].Type, Is.EqualTo(FieldType.F32LE));
    Assert.That(template.Root.Fields[5].Type, Is.EqualTo(FieldType.F64BE));
    Assert.That(template.Root.Fields[6].Type, Is.EqualTo(FieldType.Bool8));
  }

  [Test, Category("HappyPath")]
  public void Interpreter_StartOffset() {
    var source = """
      struct Test {
        u32le magic;
      };
      """;
    var template = TemplateParser.Parse(source);
    var data = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0xDEADBEEF);
    var fields = StructureInterpreter.Interpret(template, data, startOffset: 4);
    Assert.That(fields[0].DisplayValue, Does.Contain("DEADBEEF"));
    Assert.That(fields[0].Offset, Is.EqualTo(4));
  }
}
