#!/usr/bin/env sh
# Installs the two GDExtensions the playground's optional tiers need, into addons/.
#
# Neither ships with this repository on purpose: both are native extensions, one binary per platform, tens of
# megabytes, their own licences, and built against a particular Godot version. They belong to the project that uses
# them, not to the addon. This script is only here so the sample's optional tiers are one command rather than five
# manual steps.
#
# Usage: sh tools/install-extensions.sh [rapier|steam|all] [--enable-rapier]
#   all             both, the default
#   --enable-rapier also set physics/3d/physics_engine=Rapier3D in project.godot, which the Rapier tier needs.
#                   Off by default: it changes the physics engine for the whole repository, tests included.
#
# Restart Godot afterwards - an extension is loaded at startup and will not appear in a running editor.
set -eu

# Pinned rather than "latest": an extension is built against a Godot version, and a silent bump is how a working
# checkout stops working. Bump these deliberately, and check the tag really does carry a build for Godot 4.7.
# Rapier stays on v0.35.1: v0.35.2 dropped gdext's experimental-threads (appsinacup/godot-rapier-physics#612), and
# since then any call from a thread other than the main one panics and leaves the physics world broken (#614, #630).
# A C# project always has such a thread - the .NET finalizer releases Godot objects on its own - so full test runs
# failed or crashed in about one of three; on v0.35.1 they are clean. Move on once #614 is resolved.
RAPIER_VERSION=v0.35.1
RAPIER_URL="https://github.com/appsinacup/godot-rapier-physics/releases/download/${RAPIER_VERSION}/godot-rapier-3d-single.zip"

STEAM_VERSION=v4.22.1-gde
STEAM_URL="https://codeberg.org/godotsteam/godotsteam/releases/download/${STEAM_VERSION}/godotsteam-4.22.1-gdextension-plugin-4.4.zip"

# Valve's Spacewar, the app id everyone develops against before they have one of their own
STEAM_APP_ID=480

root=$(cd "$(dirname "$0")/.." && pwd)
what=all
enable_rapier=no

for arg in "$@"; do
  case $arg in
    rapier|steam|all) what=$arg ;;
    --enable-rapier) enable_rapier=yes ;;
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

install_rapier() {
  echo "Rapier ${RAPIER_VERSION} -> addons/godot-rapier3d"
  rm -rf "$root/addons/godot-rapier3d"
  # The zip wraps everything in godot-rapier-3d-single/, so one component comes off
  fetch "$RAPIER_URL" 1
  [ -d "$root/addons/godot-rapier3d/bin" ] || { echo "  no bin/ after unpacking - the release layout changed" >&2; exit 1; }

  if [ "$enable_rapier" = yes ]; then
    settings=$root/project.godot
    # project.godot strips the section name from its keys: the setting "physics/3d/physics_engine" is written as
    # "3d/physics_engine" under [physics], the same way "cracknet/events/enabled" is "events/enabled" under [cracknet].
    if grep -q '^3d/physics_engine=' "$settings"; then
      sed -i.bak 's|^3d/physics_engine=.*|3d/physics_engine="Rapier3D"|' "$settings"
      rm -f "$settings.bak"
    elif grep -q '^\[physics\]' "$settings"; then
      sed -i.bak 's|^\[physics\]|[physics]\n\n3d/physics_engine="Rapier3D"|' "$settings"
      rm -f "$settings.bak"
    else
      # No [physics] section at all, in a project that has never touched one. Godot does not care where a section
      # sits in the file, so the end is as good a place as any.
      printf '\n[physics]\n\n3d/physics_engine="Rapier3D"\n' >> "$settings"
    fi

    # Every branch above can quietly do nothing if the file is not shaped the way it expects, and a physics engine
    # that was never switched looks exactly like a broken Rapier install later on. So say so now.
    grep -q '^3d/physics_engine="Rapier3D"' "$settings" \
      || { echo "  could not set physics/3d/physics_engine in project.godot - set it by hand" >&2; exit 1; }
    echo "  project.godot now asks for Rapier3D"
  fi
}

install_steam() {
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
}

case $what in
  rapier) install_rapier ;;
  steam) install_steam ;;
  all) install_rapier; install_steam ;;
esac

echo
echo "Done. Restart Godot, then check what came up:"
if [ "$what" != steam ]; then
  echo "  <godot> --headless --path . res://test/TestRunner.tscn -- --test=PhysicsObjectTests"
fi
if [ "$what" != rapier ]; then
  echo "  <godot> --headless --path . res://examples/steam/SteamSmoke.tscn   # needs the Steam client running"
fi
if [ "$what" != steam ] && ! grep -q '^3d/physics_engine="Rapier3D"' "$root/project.godot"; then
  echo "  Rapier is installed but not selected: set physics/3d/physics_engine=\"Rapier3D\", or rerun with --enable-rapier"
fi
