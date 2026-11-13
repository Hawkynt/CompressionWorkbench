; Test DOS COM program for packer round trips.
; nasm -f bin -DNTEXT=n -DNRAND=n -DSEED=n -DCSUM=n -o T.COM tc.asm
; Prints OK when the data checksum matches the one computed at build time, BAD otherwise.
%ifndef NTEXT
%define NTEXT 40
%endif
%ifndef NRAND
%define NRAND 512
%endif
%ifndef SEED
%define SEED 12345
%endif
bits 16
org 0x100
        mov si, blob
        mov cx, blobend - blob
        xor bx, bx
.sum:   lodsb
        xor ah, ah
        add bx, ax
        rol bx, 1
        loop .sum
        mov dx, msgok
        cmp bx, [csum]
        je .out
        mov dx, msgbad
.out:   mov ah, 9
        int 0x21
        mov ax, 0x4C00
        int 0x21
msgok:  db 'OK', 13, 10, '$'
msgbad: db 'BAD', 13, 10, '$'
csum:   dw CSUM
blob:
%assign i 0
%rep NTEXT
        db 'Line of packer test text number ', '0' + (i % 10), ' with repeats.', 13, 10
%assign i i+1
%endrep
%assign r SEED
%rep NRAND
%assign r (r * 1103515245 + 12345) & 0x7FFFFFFF
        db (r >> 16) & 0xFF
%endrep
blobend:
