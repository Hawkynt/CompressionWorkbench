# MHTML reference vectors

| File | Origin |
| --- | --- |
| `chrome-blink.mhtml` | Saved by Chromium 155.0.8059.40 (Google Chrome portable, headless, Windows 11) through the DevTools `Page.captureSnapshot {format: "mhtml"}` call — the same serializer as "Save page as… Webpage, single file". The page was served from `http://127.0.0.1:8731/`: `index.html` (UTF-8 text, a stylesheet, a 4×4 PNG and an iframe), `style.css`, `img/dot.png`, `frame.html`. CRLF line breaks, quoted-printable text, base64 image. |
| `python-email.mht` | Built by CPython 3.13's stdlib `email` package (`MIMEMultipart('related')`, `BytesGenerator`, compat32 policy) from the same files: LF line breaks, a nested `multipart/alternative` holding a quoted-printable text and the HTML page, a 7bit stylesheet, a percent-encoded location (`img/my%20dot.png`), a `Content-ID`-only image, a part on another origin with a port and a query, and a part with neither location nor id. |
| `python-email-listing.txt` | CPython `email` (compat32) decoding of both files: for every leaf part its `Content-Location` (or `cid:`), decoded size and SHA-256. |

Both documents were made for this suite; they contain nothing but the test page above.
