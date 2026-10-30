# DBX reference vectors

`Inbox.dbx`, `Outbox.dbx`, `Offline.dbx` and `Folders.dbx` are real Outlook Express 6 files from the
test data of DbxOutlookExpress (<https://github.com/jamesjohnmcguire/DbxOutlookExpress>, fetched
2026-10-26), unmodified.

undbx 0.21 (Windows build) extracts exactly one message from them — `Inbox.dbx`'s "Welcome to Outlook
Express 6", 10139 bytes, SHA-256 `5690ac3f898d12554c351767385901b1281720a1b485b08057b47ced59891ec9` —
reports none in `Outbox.dbx` and says `Offline.dbx` holds no messages. `DbxTests` expects the same.

Licence of the files (MIT):

```
MIT License

Copyright (c) 2021 - 2026 James John McGuire

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
