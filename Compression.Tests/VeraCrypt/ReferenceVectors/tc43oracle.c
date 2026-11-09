/* Oracle harness: runs TrueCrypt 4.3a's own EncryptSectors on fixed inputs; see README.md. */
#include <stdio.h>
#include <string.h>
#include "Crypto.h"
int main(void) {
  static const int modes[14][2] = {{0,0},{1,2},{1,2},{1,2},{1,2},{1,2},{1,2},{1,3},{1,3},{1,3},{1,3},{1,3},{4,0},{4,0}};
  for (int ea = 1; ea <= 13; ++ea)
    for (int m = 0; m < 2; ++m) {
      int mode = modes[ea][m];
      if (!mode) continue;
      unsigned char key[256];
      for (int i = 0; i < 256; ++i) key[i] = (unsigned char)(i * 7 + 1);
      PCRYPTO_INFO ci = crypto_open();
      ci->ea = ea; ci->mode = mode; ci->hiddenVolume = 0;
      if (EAInit(ea, key + DISK_IV_SIZE, ci->ks) != 0) { printf("ERR EAInit %d\n", ea); continue; }
      memcpy(ci->iv, key, DISK_IV_SIZE);
      if (!EAInitMode(ci)) { printf("ERR mode %d %d\n", ea, mode); continue; }
      unsigned char buf[1024];
      for (int i = 0; i < 1024; ++i) buf[i] = (unsigned char)(i * 13 + 5);
      EncryptSectors((unsigned __int32 *)buf, 77, 2, ci);
      char name[100]; EAGetName(name, ea);
      printf("%d %d %s ", ea, mode, name);
      for (int i = 0; i < 1024; ++i) printf("%02x", buf[i]);
      printf("\n");
    }
  return 0;
}
