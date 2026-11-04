"""Appends a WIN_CERTIFICATE (16 bytes, revision 2.0, PKCS#7 type, dummy payload) to a PE image, points
data directory 4 at it, then appends an 8-byte overlay."""
import struct, sys
src, dst = sys.argv[1], sys.argv[2]
d = bytearray(open(src, "rb").read())
pe = struct.unpack_from("<I", d, 0x3C)[0]
opt = pe + 24
magic = struct.unpack_from("<H", d, opt)[0]
dirs = opt + (112 if magic == 0x20B else 96)
while len(d) % 8:
    d.append(0)
cert_off = len(d)
cert = struct.pack("<IHH", 16, 0x0200, 0x0002) + b"CERTDATA"
d += cert
struct.pack_into("<II", d, dirs + 4 * 8, cert_off, len(cert))
d += b"OVERLAY!"
open(dst, "wb").write(d)
