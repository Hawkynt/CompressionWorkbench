"""Writes encrypted PDF fixtures with pypdf: one blank page, one 2x2 RGB FlateDecode image XObject,
one attachment 'hello.txt'. Then reads each back with pypdf and prints what it decrypted."""
import hashlib, os, sys, zlib
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "pylib"))
import pypdf
from pypdf import PdfWriter, PdfReader
from pypdf.generic import NameObject, NumberObject, StreamObject, DictionaryObject

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
PIXELS = bytes([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255])
ATTACH = b"attached to an encrypted PDF\n"

def build(path, user, owner, algorithm):
    w = PdfWriter()
    page = w.add_blank_page(72, 72)
    img = StreamObject()
    img[NameObject("/Type")] = NameObject("/XObject")
    img[NameObject("/Subtype")] = NameObject("/Image")
    img[NameObject("/Width")] = NumberObject(2)
    img[NameObject("/Height")] = NumberObject(2)
    img[NameObject("/ColorSpace")] = NameObject("/DeviceRGB")
    img[NameObject("/BitsPerComponent")] = NumberObject(8)
    img[NameObject("/Filter")] = NameObject("/FlateDecode")
    img._data = zlib.compress(PIXELS)
    ref = w._add_object(img)
    res = DictionaryObject()
    xo = DictionaryObject()
    xo[NameObject("/Im0")] = ref
    res[NameObject("/XObject")] = xo
    page[NameObject("/Resources")] = res
    w.add_attachment("hello.txt", ATTACH)
    w.encrypt(user_password=user, owner_password=owner, algorithm=algorithm)
    with open(path, "wb") as f:
        w.write(f)

cases = [
    ("pypdf-rc4-40.pdf", "", "owner", "RC4-40"),
    ("pypdf-rc4-128.pdf", "", "owner", "RC4-128"),
    ("pypdf-aes-128.pdf", "", "owner", "AES-128"),
    ("pypdf-aes-256.pdf", "", "owner", "AES-256"),
    ("pypdf-aes-256-r5.pdf", "", "owner", "AES-256-R5"),
    ("pypdf-rc4-128-userpw.pdf", "user", "owner", "RC4-128"),
]
print("pypdf", pypdf.__version__)
for name, user, owner, alg in cases:
    p = os.path.join(out, name)
    build(p, user, owner, alg)
    r = PdfReader(p)
    r.decrypt(user)
    att = r.attachments["hello.txt"][0]
    img = r.pages[0]["/Resources"]["/XObject"]["/Im0"].get_object().get_data()
    enc = r.trailer["/Encrypt"].get_object()
    print(name, os.path.getsize(p), "V", enc.get("/V"), "R", enc.get("/R"), att == ATTACH, img == PIXELS)
