#!/bin/bash
# Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
#
# Makes the Linux sysroot that build.py --linux cross-compiles libNewAANgx.so against: the headers and libraries of glibc 2.31 and
# GCC 9's libstdc++ from Ubuntu 20.04, so the plugin runs on any distribution with glibc 2.31 or newer.
#
# Run it on Ubuntu or Debian, for example in WSL:
#     bash Native/make_linux_sysroot.sh /mnt/c/Users/<you>/newaa-sysroot
# then build on Windows with
#     python build.py --linux --native --linux-sysroot C:\Users\<you>\newaa-sysroot
#
# It needs no root: apt downloads the packages into a private folder and checks them against the Ubuntu archive's signatures,
# and dpkg -x unpacks them. The copy in the output folder has every symlink replaced by the file it points to inside the
# sysroot, so Windows tools can read it.
set -euo pipefail

if [ $# -ne 1 ]; then
    echo "usage: $0 <output folder>" >&2
    exit 2
fi
OUT="$1"
WORK="${XDG_CACHE_HOME:-$HOME/.cache}/newaa-linux-sysroot"
KEYRING=/usr/share/keyrings/ubuntu-archive-keyring.gpg
PACKAGES=(libc6 libc6-dev linux-libc-dev libgcc-9-dev libgcc-s1 libstdc++-9-dev libstdc++6 libcrypt-dev)

if [ ! -f "$KEYRING" ]; then
    echo "$KEYRING is missing. Install the ubuntu-keyring package, or run this on Ubuntu." >&2
    exit 1
fi

rm -rf "$WORK"
mkdir -p "$WORK/lists/partial" "$WORK/cache/archives/partial" "$WORK/debs" "$WORK/root"
: > "$WORK/status"
cat > "$WORK/sources.list" <<EOF
deb [arch=amd64 signed-by=$KEYRING] http://archive.ubuntu.com/ubuntu focal main
deb [arch=amd64 signed-by=$KEYRING] http://archive.ubuntu.com/ubuntu focal-updates main
deb [arch=amd64 signed-by=$KEYRING] http://security.ubuntu.com/ubuntu focal-security main
EOF
APT=(apt-get -q
     -o Dir::Etc::SourceList="$WORK/sources.list" -o Dir::Etc::SourceParts=/nonexistent
     -o Dir::State::Lists="$WORK/lists" -o Dir::Cache="$WORK/cache" -o Dir::State::status="$WORK/status"
     -o APT::Sandbox::User="$(id -un)" -o APT::Architecture=amd64 -o Debug::NoLocking=1 -o Acquire::Languages=none)

echo "Downloading Ubuntu 20.04 package lists ..."
"${APT[@]}" update > "$WORK/update.log"
echo "Downloading ${PACKAGES[*]} ..."
(cd "$WORK/debs" && "${APT[@]}" download "${PACKAGES[@]}" > "$WORK/download.log")

for deb in "$WORK"/debs/*.deb; do
    dpkg -x "$deb" "$WORK/root"
done
# Absolute links point into the system that runs this script. Point them into the sysroot instead.
find "$WORK/root" -type l | while read -r link; do
    target=$(readlink "$link")
    case "$target" in
        /*) ln -sfn "$WORK/root$target" "$link" ;;
    esac
done

rm -rf "$OUT"
for dir in usr/include usr/lib/x86_64-linux-gnu usr/lib/gcc/x86_64-linux-gnu lib/x86_64-linux-gnu lib64; do
    if [ -e "$WORK/root/$dir" ]; then
        mkdir -p "$OUT/$dir"
        # Links to files outside the packages (documentation and the like) cannot be copied and are not needed.
        cp -rL "$WORK/root/$dir/." "$OUT/$dir/" 2>/dev/null || true
    fi
done
rm -rf "$WORK"

for needed in usr/include/stdio.h usr/lib/x86_64-linux-gnu/libc.so lib/x86_64-linux-gnu/libc.so.6 usr/lib/gcc/x86_64-linux-gnu/9/libstdc++.a; do
    if [ ! -f "$OUT/$needed" ]; then
        echo "The sysroot is incomplete: $needed is missing." >&2
        exit 1
    fi
done
echo "Linux sysroot ready in $OUT ($(du -sh "$OUT" | cut -f1))."
