from pathlib import Path

codec = Path('Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.BeeGfs/BeeGfsMetadataCodec.cs')
text = codec.read_text(encoding='utf-8')
old = '''    var state = (inodeFeatures & FileInodeFeatureHasStateFlags) != 0 ? reader.ReadByte() : (byte)0;\n    reader.Skip(3);\n'''
new = '''    byte state;\n    if ((inodeFeatures & FileInodeFeatureHasStateFlags) != 0) {\n      state = reader.ReadByte();\n      reader.Skip(3);\n    } else {\n      state = 0;\n      reader.Skip(4);\n    }\n'''
if old not in text:
  raise SystemExit('V6 inode state alignment marker not found')
codec.write_text(text.replace(old, new, 1), encoding='utf-8')

tests = Path('Compression.Tests/BeeGfs/BeeGfsMetadataCodecTests.cs')
text = tests.read_text(encoding='utf-8')
old = '''    Assert.Throws<Exception>(() => BeeGfsMetadataCodec.ParseDentry(truncated, "root"));\n'''
new = '''    Assert.That(\n      () => BeeGfsMetadataCodec.ParseDentry(truncated, "root"),\n      Throws.Exception);\n'''
if old not in text:
  raise SystemExit('truncation assertion marker not found')
tests.write_text(text.replace(old, new, 1), encoding='utf-8')
