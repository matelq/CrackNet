#!/usr/bin/env sh
# Installs the GDExtension the playground's optional Steam tier needs, into addons/.
#
# It does not ship with this repository on purpose: a native extension is one binary per platform, tens of megabytes,
# its own licence, and built against a particular Godot version. It belongs to the project that uses it, not to the
# addon. This script is only here so the sample's Steam tier is one command rather than five manual steps.
#
# Usage: sh tools/install-extensions.sh [steam]
#
# Physics needs nothing installed: the project runs on Jolt, which is built into Godot (#81, #84). Another engine as
# an option is #68.
#
# Restart Godot afterwards - an extension is loaded at startup and will not appear in a running editor.
set -eu

# Pinned rather than "latest": an extension is built against a Godot version, and a silent bump is how a working
# checkout stops working. Bump deliberately, and check the tag really does carry a build for Godot 4.7.
STEAM_VERSION=v4.22.1-gde
STEAM_URL="https://codeberg.org/godotsteam/godotsteam/releases/download/${STEAM_VERSION}/godotsteam-4.22.1-gdextension-plugin-4.4.zip"

# Valve's Spacewar, the app id everyone develops against before they have one of their own
STEAM_APP_ID=480

root=$(cd "$(dirname "$0")/.." && pwd)

for arg in "$@"; do
  case $arg in
    steam) ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done

command -v curl >/dev/null || { echo "curl is required" >&2; exit 1; }
python=$(command -v python3 || command -v python) \
  || { echo "python is required, for unzipping - unzip is not on every machine this has to run on" >&2; exit 1; }

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# A progress bar is only worth anything on a terminal; in a log it is one very long line
if [ -t 2 ]; then progress=--progress-bar; else progress=--silent; fi

# Unpacks a zip into $root, dropping the first $2 path components. -f so a moved release fails here rather than
# unpacking a 404 page.
fetch() {
  url=$1
  strip=$2
  echo "  fetching $url"
  curl -fL --show-error "$progress" -o "$work/download.zip" "$url"
  "$python" - "$work/download.zip" "$root" "$strip" <<'PY'
import os, sys, zipfile

archive, destination, strip = sys.argv[1], sys.argv[2], int(sys.argv[3])
with zipfile.ZipFile(archive) as zf:
    for entry in zf.infolist():
        parts = entry.filename.split("/")[strip:]
        if not parts or not parts[-1]:
            continue
        target = os.path.join(destination, *parts)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with zf.open(entry) as source, open(target, "wb") as sink:
            sink.write(source.read())
PY
}

echo "GodotSteam ${STEAM_VERSION} -> addons/godotsteam"
rm -rf "$root/addons/godotsteam"
# This one is already rooted at addons/
fetch "$STEAM_URL" 0
[ -f "$root/addons/godotsteam/godotsteam.gdextension" ] || { echo "  no godotsteam.gdextension after unpacking - the release layout changed" >&2; exit 1; }

# Steam reads this from the working directory of whatever process asks it to initialise, which is the project
# directory for a run from the editor and the binary's own directory for the editor itself. Both, then.
echo "$STEAM_APP_ID" > "$root/steam_appid.txt"
for binary in "$root"/.tools/godot/*/; do
  if [ -d "$binary" ]; then echo "$STEAM_APP_ID" > "$binary/steam_appid.txt"; fi
done
echo "  steam_appid.txt written with app id $STEAM_APP_ID"

echo
echo "Done. Restart Godot, then check what came up:"
echo "  <godot> --headless --path . res://examples/steam/SteamSmoke.tscn   # needs the Steam client running"
