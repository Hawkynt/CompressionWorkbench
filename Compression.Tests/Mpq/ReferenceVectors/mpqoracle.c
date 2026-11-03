/*
 * mpqoracle - a thin command-line front end over StormLib (MIT), used as the
 * third-party oracle for CompressionWorkbench's MPQ reader, writer and codecs.
 *
 *   mpqoracle create  <out.mpq> <v1|v2|v1a> <local>=<archived>:<flagsHex>:<maskHex> ...
 *                     (mask "w0".."w2" = SFileAddWave with that quality)
 *   mpqoracle extract <in.mpq> <outdir>      prints name<TAB>size<TAB>flags<TAB>status
 *   mpqoracle verify  <in.mpq>               prints name<TAB>SFileVerifyFile result
 *   mpqoracle comp    <maskHex> <cmpType> <level> <in> <out>
 *   mpqoracle decomp  <outSize> <in> <out>
 *   mpqoracle implode <in> <out>
 *   mpqoracle explode <outSize> <in> <out>
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "StormLib.h"

static unsigned char *load(const char *path, long *len) {
  FILE *f = fopen(path, "rb");
  if (!f) { fprintf(stderr, "cannot open %s\n", path); exit(2); }
  fseek(f, 0, SEEK_END); *len = ftell(f); fseek(f, 0, SEEK_SET);
  unsigned char *buf = malloc(*len + 16);
  if (*len && fread(buf, 1, *len, f) != (size_t)*len) { fprintf(stderr, "read failed\n"); exit(2); }
  fclose(f);
  return buf;
}

static void save(const char *path, const void *data, long len) {
  FILE *f = fopen(path, "wb");
  if (!f) { fprintf(stderr, "cannot create %s\n", path); exit(2); }
  if (len) fwrite(data, 1, len, f);
  fclose(f);
}

static int create(int argc, char **argv) {
  DWORD flags = MPQ_CREATE_LISTFILE;
  if (strncmp(argv[3], "v2", 2) == 0) flags |= MPQ_CREATE_ARCHIVE_V2;
  if (strchr(argv[3], 'a')) flags |= MPQ_CREATE_ATTRIBUTES;
  HANDLE mpq;
  remove(argv[2]);
  if (!SFileCreateArchive(argv[2], flags, 64, &mpq)) { fprintf(stderr, "create failed %u\n", GetLastError()); return 1; }
  for (int i = 4; i < argc; i++) {
    char spec[1024];
    strncpy(spec, argv[i], sizeof spec - 1); spec[sizeof spec - 1] = 0;
    char *eq = strchr(spec, '=');
    char *c1 = eq ? strchr(eq, ':') : NULL;
    char *c2 = c1 ? strchr(c1 + 1, ':') : NULL;
    if (!eq || !c1 || !c2) { fprintf(stderr, "bad spec %s\n", argv[i]); return 1; }
    *eq = 0; *c1 = 0; *c2 = 0;
    DWORD fileFlags = strtoul(c1 + 1, NULL, 16);
    bool ok;
    if (c2[1] == 'w')
      ok = SFileAddWave(mpq, spec, eq + 1, fileFlags, (DWORD)atoi(c2 + 2));
    else {
      DWORD mask = strtoul(c2 + 1, NULL, 16);
      ok = SFileAddFileEx(mpq, spec, eq + 1, fileFlags, mask, mask);
    }
    if (!ok) { fprintf(stderr, "add %s failed %u\n", eq + 1, GetLastError()); return 1; }
  }
  if (!SFileCloseArchive(mpq)) { fprintf(stderr, "close failed %u\n", GetLastError()); return 1; }
  return 0;
}

static int extract(const char *mpqPath, const char *outDir) {
  HANDLE mpq;
  if (!SFileOpenArchive(mpqPath, 0, MPQ_OPEN_READ_ONLY | MPQ_OPEN_CHECK_SECTOR_CRC, &mpq)) {
    fprintf(stderr, "open failed %u\n", GetLastError()); return 1;
  }
  SFILE_FIND_DATA fd;
  HANDLE find = SFileFindFirstFile(mpq, "*", &fd, NULL);
  int failures = 0;
  while (find) {
    HANDLE file;
    const char *status = "OK";
    if (SFileOpenFileEx(mpq, fd.cFileName, 0, &file)) {
      DWORD size = SFileGetFileSize(file, NULL);
      unsigned char *buf = malloc(size + 1);
      DWORD read = 0;
      if (!SFileReadFile(file, buf, size, &read, NULL) || read != size) { status = "READ-ERROR"; failures++; }
      else if (outDir) {
        char name[MAX_PATH], path[2 * MAX_PATH];
        strcpy(name, fd.cFileName);
        for (char *p = name; *p; p++) if (*p == '\\' || *p == '/') *p = '#';
        snprintf(path, sizeof path, "%s/%s", outDir, name);
        save(path, buf, size);
      }
      free(buf);
      SFileCloseFile(file);
    } else { status = "OPEN-ERROR"; failures++; }
    printf("%s\t%u\t%08X\t%s\n", fd.cFileName, fd.dwFileSize, fd.dwFileFlags, status);
    if (!SFileFindNextFile(find, &fd)) break;
  }
  if (find) SFileFindClose(find);
  SFileCloseArchive(mpq);
  return failures ? 3 : 0;
}

static int verify(const char *mpqPath) {
  HANDLE mpq;
  if (!SFileOpenArchive(mpqPath, 0, MPQ_OPEN_READ_ONLY, &mpq)) { fprintf(stderr, "open failed %u\n", GetLastError()); return 1; }
  SFILE_FIND_DATA fd;
  HANDLE find = SFileFindFirstFile(mpq, "*", &fd, NULL);
  int failures = 0;
  while (find) {
    DWORD r = SFileVerifyFile(mpq, fd.cFileName, SFILE_VERIFY_ALL);
    if (r & (VERIFY_OPEN_ERROR | VERIFY_READ_ERROR | VERIFY_FILE_SECTOR_CRC_ERROR | VERIFY_FILE_CHECKSUM_ERROR | VERIFY_FILE_MD5_ERROR)) failures++;
    printf("%s\t%08X\n", fd.cFileName, r);
    if (!SFileFindNextFile(find, &fd)) break;
  }
  if (find) SFileFindClose(find);
  SFileCloseArchive(mpq);
  return failures ? 3 : 0;
}

int main(int argc, char **argv) {
  if (argc < 3) { fprintf(stderr, "usage: see source\n"); return 2; }
  const char *cmd = argv[1];
  if (!strcmp(cmd, "create") && argc >= 4) return create(argc, argv);
  if (!strcmp(cmd, "extract")) return extract(argv[2], argc > 3 ? argv[3] : NULL);
  if (!strcmp(cmd, "verify")) return verify(argv[2]);
  long len;
  if (!strcmp(cmd, "comp") && argc == 7) {
    unsigned char *in = load(argv[5], &len);
    int outLen = (int)len + 64;
    unsigned char *out = malloc(outLen);
    if (!SCompCompress(out, &outLen, in, (int)len, strtoul(argv[2], NULL, 16), atoi(argv[3]), atoi(argv[4]))) return 1;
    save(argv[6], out, outLen);
    return 0;
  }
  if (!strcmp(cmd, "decomp") && argc == 5) {
    unsigned char *in = load(argv[3], &len);
    int outLen = atoi(argv[2]);
    unsigned char *out = malloc(outLen + 1);
    if (!SCompDecompress(out, &outLen, in, (int)len)) return 1;
    save(argv[4], out, outLen);
    return 0;
  }
  if (!strcmp(cmd, "implode") && argc == 4) {
    unsigned char *in = load(argv[2], &len);
    int outLen = (int)len * 2 + 64;
    unsigned char *out = malloc(outLen);
    unsigned char *big = malloc(outLen);
    /* SCompImplode refuses to grow the data; call with a large buffer so the stream is always kept. */
    memcpy(big, in, len);
    if (!SCompImplode(out, &outLen, big, (int)len)) return 1;
    save(argv[3], out, outLen);
    return 0;
  }
  if (!strcmp(cmd, "explode") && argc == 5) {
    unsigned char *in = load(argv[3], &len);
    int outLen = atoi(argv[2]);
    unsigned char *out = malloc(outLen + 1);
    if (!SCompExplode(out, &outLen, in, (int)len)) return 1;
    save(argv[4], out, outLen);
    return 0;
  }
  fprintf(stderr, "unknown command\n");
  return 2;
}
