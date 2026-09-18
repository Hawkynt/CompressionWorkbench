#!/usr/bin/env bash
# Captures one documentation screenshot of the NativeForms shell.
#
# The WPF shell rendered its own visual tree to a PNG. NativeForms has no equivalent — nothing in
# it draws a window to a bitmap — so the app only builds the fixture and shows the window, and the
# picture is taken from outside, here. Everything that made the old captures reproducible still
# lives in the app: fixed payloads, fixed timestamps, a fixed scramble seed, and an analysis window
# that leaves its elapsed time out of the status line.
#
# Usage: capture-screenshot.sh <target> <output.png>
#   target: archive-browser | analysis | maintenance
set -euo pipefail

target="$1"
output="$2"
display=":99"

mkdir -p "$(dirname "$output")"

Xvfb "$display" -screen 0 1200x760x24 -nolisten tcp &
xvfb_pid=$!
trap 'kill "$xvfb_pid" 2>/dev/null || true' EXIT

# Give the server a moment to accept connections before the app tries to map a window.
for _ in $(seq 1 50); do
  DISPLAY="$display" xdpyinfo >/dev/null 2>&1 && break
  sleep 0.2
done

DISPLAY="$display" dotnet "$APP_BINARY" "--screenshot=$target" &
app_pid=$!

# The analysis fixture runs a full scan before its window has anything to show, so the settle time
# is generous rather than tight. A shorter wait produced half-drawn grids.
sleep 25

if ! kill -0 "$app_pid" 2>/dev/null; then
  echo "::error::The shell exited before the $target screenshot could be taken."
  exit 1
fi

DISPLAY="$display" import -window root -quality 95 "$output"
kill "$app_pid" 2>/dev/null || true
wait "$app_pid" 2>/dev/null || true

if [ ! -s "$output" ]; then
  echo "::error::No screenshot was written to $output."
  exit 1
fi

echo "Captured $target -> $output ($(stat -c%s "$output") bytes)"
