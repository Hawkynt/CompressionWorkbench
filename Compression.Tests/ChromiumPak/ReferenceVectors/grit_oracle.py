# ReadDataPackFromString / WriteDataPackToString from Chromium's tools/grit/grit/format/data_pack.py
# (main, fetched 2026-10-26; v4 writer from revision 1be9fc91a8947739a44e1f2e9eb2cd173b42ef95, bytes
# instead of str). Copyright 2012 The Chromium Authors, BSD-3-Clause. Used here as a black-box oracle.
import hashlib, struct, sys

PACK_FILE_VERSION = 5
BINARY, UTF8, UTF16 = range(3)

def ReadDataPackFromString(data):
  version = struct.unpack('<I', data[:4])[0]
  if version == 4:
    resource_count, encoding = struct.unpack('<IB', data[4:9])
    alias_count = 0
    header_size = 9
  elif version == 5:
    encoding, resource_count, alias_count = struct.unpack('<BxxxHH', data[4:12])
    header_size = 12
  else:
    raise Exception('Found version: ' + str(version))
  resources = {}
  kIndexEntrySize = 2 + 4
  def entry_at_index(idx):
    offset = header_size + idx * kIndexEntrySize
    return struct.unpack('<HI', data[offset : offset + kIndexEntrySize])
  prev_resource_id, prev_offset = entry_at_index(0)
  for i in range(1, resource_count + 1):
    resource_id, offset = entry_at_index(i)
    resources[prev_resource_id] = data[prev_offset:offset]
    prev_resource_id, prev_offset = resource_id, offset
  id_table_size = (resource_count + 1) * kIndexEntrySize
  kAliasEntrySize = 2 + 2
  def alias_at_index(idx):
    offset = header_size + id_table_size + idx * kAliasEntrySize
    return struct.unpack('<HH', data[offset : offset + kAliasEntrySize])
  aliases = {}
  for i in range(alias_count):
    resource_id, index = alias_at_index(i)
    aliased_id = entry_at_index(index)[0]
    aliases[resource_id] = aliased_id
    resources[resource_id] = resources[aliased_id]
  return version, encoding, resources, aliases

def WriteDataPackToString(resources, encoding):
  ret = []
  resource_ids = sorted(resources)
  id_by_data = {resources[k]: k for k in reversed(resource_ids)}
  alias_map = {k: id_by_data[v] for k, v in resources.items() if id_by_data[v] != k}
  resource_count = len(resources) - len(alias_map)
  ret.append(struct.pack('<IBxxxHH', PACK_FILE_VERSION, encoding, resource_count, len(alias_map)))
  HEADER_LENGTH = 4 + 4 + 2 + 2
  data_offset = HEADER_LENGTH + (resource_count + 1) * 6 + len(alias_map) * 4
  index_by_id = {}
  deduped_data = []
  index = 0
  for resource_id in resource_ids:
    if resource_id in alias_map:
      continue
    data = resources[resource_id]
    index_by_id[resource_id] = index
    ret.append(struct.pack('<HI', resource_id, data_offset))
    data_offset += len(data)
    deduped_data.append(data)
    index += 1
  ret.append(struct.pack('<HI', 0, data_offset))
  for resource_id in sorted(alias_map):
    index = index_by_id[alias_map[resource_id]]
    ret.append(struct.pack('<HH', resource_id, index))
  ret.extend(deduped_data)
  return b''.join(ret)

def WriteDataPackToStringV4(resources, encoding):
  ids = sorted(resources.keys())
  ret = []
  ret.append(struct.pack('<IIB', 4, len(ids), encoding))
  HEADER_LENGTH = 2 * 4 + 1
  index_length = (len(ids) + 1) * (2 + 4)
  data_offset = HEADER_LENGTH + index_length
  for id in ids:
    ret.append(struct.pack('<HI', id, data_offset))
    data_offset += len(resources[id])
  ret.append(struct.pack('<HI', 0, data_offset))
  for id in ids:
    ret.append(resources[id])
  return b''.join(ret)

def listing(path):
  version, encoding, resources, aliases = ReadDataPackFromString(open(path, 'rb').read())
  name = path.replace('\\', '/').split('/')[-1]
  out = ['# %s version=%d encoding=%d resources=%d aliases=%d' % (name, version, encoding, len(resources), len(aliases))]
  for rid in sorted(resources):
    out.append('%s %d %d %s %s' % (name, rid, len(resources[rid]), hashlib.sha256(resources[rid]).hexdigest(),
                                   aliases.get(rid, '-')))
  return out

if __name__ == '__main__':
  if sys.argv[1] == 'make':
    d = sys.argv[2]
    png = bytes.fromhex('89504e470d0a1a0a') + bytes(range(256)) * 2
    res = {101: b'hello pak', 102: png, 103: b'hello pak', 200: b'', 4096: 'grüße'.encode('utf-8'), 65535: b'last'}
    open(d + '/grit-v5-utf8.pak', 'wb').write(WriteDataPackToString(res, UTF8))
    open(d + '/grit-v5-binary.pak', 'wb').write(WriteDataPackToString({1: png, 2: b'\x00\x01\x02'}, BINARY))
    open(d + '/grit-v5-empty.pak', 'wb').write(WriteDataPackToString({}, UTF8))
    open(d + '/grit-v4-utf8.pak', 'wb').write(WriteDataPackToStringV4({7: b'seven', 8: b'', 300: png}, UTF8))
  elif sys.argv[1] == 'write':
    # write <out> <encoding> <id=hexfile>... : pack the given files
    res = {}
    for spec in sys.argv[4:]:
      rid, f = spec.split('=', 1)
      res[int(rid)] = open(f, 'rb').read()
    open(sys.argv[2], 'wb').write(WriteDataPackToString(res, int(sys.argv[3])))
  else:
    for p in sys.argv[2:]:
      print('\n'.join(listing(p)))
