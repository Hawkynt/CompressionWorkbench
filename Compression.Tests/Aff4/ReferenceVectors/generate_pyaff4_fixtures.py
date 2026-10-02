"""Writes the pyaff4 logical-image fixtures in this folder and the oracle listing beside them.

Run with pyaff4 (github.com/aff4/pyaff4, master 6a911586) importable and this folder as the working
directory:  python generate_pyaff4_fixtures.py

Two knobs are turned so that small inputs exercise everything a large logical image does:
  * WritableLogicalImageContainer.maxSegmentResidentSize drops from 1 MiB to 40000 bytes, so a
    100 KB file is written as an aff4:ImageStream instead of a ZipSegment;
  * the ImageStream keeps 2 chunks per bevy (pyaff4 default 1024), so it spans several bevies.
The ImageStream compression is snappy (pyaff4's own choice) for one fixture and zlib for the other.
Everything else is pyaff4's ordinary logical-image writer.

The listing (pyaff4-expected.tsv) is produced by pyaff4 reading the fixture back, not by us.
"""
import hashlib, os, random, shutil, sys

from pyaff4 import container, data_store, hashes, lexicon, linear_hasher, logical, rdfvalue, utils, zip
from pyaff4 import aff4_image, escaping

SOURCE = "aff4src"


def build_source():
    shutil.rmtree(SOURCE, ignore_errors=True)
    os.makedirs(os.path.join(SOURCE, "evidence", "deep"))
    os.makedirs(os.path.join(SOURCE, "emptydir"))
    rnd = random.Random(416)
    with open(os.path.join(SOURCE, "evidence", "hello.txt"), "wb") as f:
        f.write(b"hello world\n")
    with open(os.path.join(SOURCE, "evidence", "Grüße ネコ.txt"), "wb") as f:
        f.write("umlaut ü\n".encode("utf-8"))
    with open(os.path.join(SOURCE, "empty.bin"), "wb"):
        pass
    with open(os.path.join(SOURCE, "evidence", "deep", "small.bin"), "wb") as f:
        f.write(bytes(rnd.getrandbits(8) for _ in range(3000)))
    # 32768 random bytes (a chunk stored raw), two compressible chunks, a partial padded tail.
    big = bytes(rnd.getrandbits(8) for _ in range(32768))
    big += (b"AFF4 ImageStream chunk " * 3000)[: 2 * 32768]
    big += b"tail" * 1234
    with open(os.path.join(SOURCE, "evidence", "deep", "stream.bin"), "wb") as f:
        f.write(big)


def patch(compression):
    container.WritableLogicalImageContainer.maxSegmentResidentSize = 40000

    def new_block_stream(self, image_urn, filename):
        stream = aff4_image.AFF4Image.NewAFF4Image(self.resolver, image_urn, self.urn)
        stream.compression = compression
        stream.chunks_per_segment = 2
        return stream

    def write_block_stream(self, image_urn, filename, readstream):
        with new_block_stream(self, image_urn, filename) as stream:
            stream.WriteStream(readstream)

    container.WritableLogicalImageContainer.newCompressedBlockStream = new_block_stream
    container.WritableLogicalImageContainer.writeCompressedBlockStream = write_block_stream


def create(target, compression, zip_method):
    patch(compression)
    if os.path.exists(target):
        os.remove(target)
    cwd = os.getcwd()
    target = os.path.abspath(target)
    os.chdir(SOURCE)
    try:
        with data_store.MemoryDataStore() as resolver:
            urn = rdfvalue.URN.FromFileName(target)
            with container.Container.createURN(resolver, urn, compression_method=zip_method) as volume:
                pending = ["evidence", "empty.bin", "emptydir"]
                for path in pending:
                    path = utils.SmartUnicode(path)
                    meta = logical.FSMetadata.create(path)
                    if os.path.isdir(path):
                        image = volume.urn.Append(escaping.arnPathFragment_from_path(path), quote=False)
                        meta.urn = image
                        meta.store(resolver)
                        resolver.Set(volume.urn, image, rdfvalue.URN(lexicon.standard11.pathName), rdfvalue.XSDString(path))
                        resolver.Add(volume.urn, image, rdfvalue.URN(lexicon.AFF4_TYPE), rdfvalue.URN(lexicon.standard11.FolderImage))
                        resolver.Add(volume.urn, image, rdfvalue.URN(lexicon.AFF4_TYPE), rdfvalue.URN(lexicon.standard.Image))
                        pending.extend(os.path.join(path, child) for child in sorted(os.listdir(path)))
                        continue
                    with open(path, "rb") as src:
                        hasher = linear_hasher.StreamHasher(src, [lexicon.HASH_SHA1, lexicon.HASH_MD5, lexicon.HASH_SHA256])
                        image = volume.writeLogicalStream(path, hasher, meta.length)
                        meta.urn = image
                        meta.store(resolver)
                        for h in hasher.hashes:
                            resolver.Add(image, image, rdfvalue.URN(lexicon.standard.hash),
                                         hashes.newImmutableHash(h.hexdigest(), hasher.hashToType[h]))
    finally:
        os.chdir(cwd)


def oracle(path):
    """What pyaff4 itself reads back: one (name, size, sha256) row per logical file."""
    rows = []
    with container.Container.openURNtoContainer(rdfvalue.URN.FromFileName(path)) as volume:
        for image in volume.images():
            with volume.resolver.AFF4FactoryOpen(image.urn) as stream:
                data = stream.read(1 << 30)
            rows.append((os.path.basename(path), str(image.name()), len(data), hashlib.sha256(data).hexdigest()))
    return sorted(rows)


if __name__ == "__main__":
    build_source()
    create("pyaff4-logical-snappy.aff4", lexicon.AFF4_IMAGE_COMPRESSION_SNAPPY, zip.ZIP_DEFLATE)
    create("pyaff4-logical-zlib-stored.aff4", lexicon.AFF4_IMAGE_COMPRESSION_ZLIB, zip.ZIP_STORED)
    shutil.rmtree(SOURCE)
    rows = oracle("pyaff4-logical-snappy.aff4") + oracle("pyaff4-logical-zlib-stored.aff4") + oracle("dream.aff4")
    with open("pyaff4-expected.tsv", "w", encoding="utf-8", newline="\n") as f:
        for row in rows:
            f.write("%s\t%s\t%d\t%s\n" % row)
    sys.stdout.write(open("pyaff4-expected.tsv", encoding="utf-8").read())
