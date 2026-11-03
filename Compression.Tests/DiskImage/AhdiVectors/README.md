# Atari AHDI reference disk

`ahdi.img.gz` is a 64 MiB disk image labelled by GNU parted 3.6 (Ubuntu 24.04), gzip-compressed:

```sh
truncate -s 64M ahdi.img
parted -s ahdi.img mklabel atari \
  mkpart primary fat16 2048s 10239s mkpart primary 10240s 20479s \
  mkpart extended 20480s 120000s \
  mkpart logical 22528s 40959s mkpart logical 43008s 60000s mkpart logical 62000s 80000s
echo hello atari > hello.txt
mkfs.vfat --offset=2048 ahdi.img 4096;   mcopy -i ahdi.img@@1M hello.txt ::/HELLO.TXT
mkfs.vfat --offset=43008 ahdi.img 8496;  mcopy -i ahdi.img@@22020096 hello.txt ::/LOGICAL.TXT
```

So the root sector holds two data records and an `XGM` record, and the extended chain has three
auxiliary root sectors, each with one data record and (but the last) a link to the next.

`partx.txt` is what util-linux 2.39.3 `partx -g -o NR,START,SECTORS ahdi.img` lists for it; parted
reports the same starts and sizes. The tests require our table reader to find exactly those five
partitions.
