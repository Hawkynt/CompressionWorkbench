from pathlib import Path

path = Path("Hawkynt.FileFormats.Archives/README.md")
text = path.read_text(encoding="utf-8")
marker = "### 🧬 Structured data and registry"
anchor = "### 📦 Software packages and installers"

if marker not in text:
    section = r'''### 🧬 Structured data and registry

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| JSON | `Json` | `.json` | WORM | ✅ | — | RFC 8259 object/array projection; CWB-created binary leaves use a versioned base64 envelope | [RFC 8259](https://www.rfc-editor.org/rfc/rfc8259) |
| XML | `Xml` | `.xml` | WORM | ✅ | — | XML 1.0 projection with DTD/external-entity resolution disabled; CWB creation uses a namespaced archive envelope | [W3C XML 1.0](https://www.w3.org/TR/xml/) |
| MessagePack | `MessagePack` | `.msgpack` `.mpk` | WORM | ✅ | — | Maps, arrays, scalars and extension payloads; managed reader/writer | [MessagePack specification](https://github.com/msgpack/msgpack/blob/master/spec.md) |
| Python pickle | `Pickle` | `.pkl` `.pickle` | WORM | ✅ | — | Non-executing opcode VM; never imports modules or invokes `GLOBAL` / `REDUCE` / `BUILD` callables | [Python](https://docs.python.org/3/library/pickle.html) |
| Perl Storable | `Storable` | `.storable` `.sto` | WORM | ✅ | — | Safe portable/network-order subset; executable, blessed and tied forms are rejected | [Perl Storable](https://perldoc.perl.org/Storable) |
| MS-NRBF / BinaryFormatter | `Nrbf` | `.nrbf` | R | ✅ | — | Non-instantiating MS-NRBF reader; no assembly loading, constructors or callbacks | [MS-NRBF](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-nrbf/) |
| Windows Registry export | `Reg` | `.reg` | WORM | ✅ | — | Offline `REGEDIT4` / Registry Editor text interchange; never touches the live registry | [Microsoft](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/reg-export) |
| Windows 9x Registry hive (CREG) | `Creg` | `.dat` `.dao` `.pol` | R | ✅ | — | Windows 95/98/Me binary hives; read-only until a native writer oracle exists | [libcreg format notes](https://github.com/libyal/libcreg/blob/main/documentation/Windows%209x%20Registry%20File%20(CREG)%20format.asciidoc) |
| Windows NT-family Registry hive (REGF) | `Regf` | `.hiv` `.hive` `.hve` | R | ✅ | — | REGF 1.1–1.6, including legacy NT and modern standard/latest layouts; read-only until native writer validation exists | [Microsoft Registry files](https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-hive) |

'''
    if anchor not in text:
        raise SystemExit(f"README anchor not found: {anchor}")
    text = text.replace(anchor, section + anchor, 1)
    path.write_text(text, encoding="utf-8")
