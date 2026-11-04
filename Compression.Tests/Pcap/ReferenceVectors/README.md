# pcap / pcapng reference vectors

Written on 2026-10-30 in WSL (Ubuntu 24.04) by Wireshark 4.2.2's own tools:

| File | Origin |
| --- | --- |
| `frame0.bin`, `frame1.bin`, `frame2.bin` | three test frames of 60, 67 and 74 bytes (byte i of frame p = `i*13+p`). |
| `text2pcap.pcap` | `text2pcap -q -F pcap` of a hex dump of the three frames: little-endian, microsecond, Ethernet, snaplen 262144. |
| `text2pcap.pcapng` | `text2pcap -q -n` of the same dump. |
| `editcap-nsec.pcap` | `editcap -F nsecpcap text2pcap.pcap`. |
| `editcap.pcapng` | `editcap -F pcapng text2pcap.pcap`. |

`tshark -r <file> -T fields -e frame.time_epoch -e frame.cap_len` lists every file as the three frames
at `1793347013.000001`, `.000002` and `.000003` seconds; `capinfos` names the file types above. Our pcap
writer must reproduce both `.pcap` files byte for byte.
