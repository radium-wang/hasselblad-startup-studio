"""Generate an isolated X2D II static-logo transaction; never execute it.

The host must save a first backup and preflight the exact USB/firmware profile.
The upload channel and GUI integration are deliberately outside this module.
"""
from __future__ import annotations

from dataclasses import dataclass
import datetime
import hashlib
import json
import re

from g2profile import HEADER, SIZE, STOCK

ROOT = "/factory_data/.hss-startup2"
LOGO = "/factory_data/logo.bin"
LABEL = "u:object_r:system_data_file:s0"
GUI = "ea2cf8a5f6209f301971f55dd1ef86882c09db04b2e60371d2a243b79f1d8098"
OWNED_NAMES = ("owner-id", "journal.json", "journal.json.next", "journal.json.b64", "journal.json.next.b64",
               "image.bin", "image.bin.b64", "image.bin.gz", "install.sh", "install.sh.b64",
               "restore.sh", "restore.sh.b64", "finish.sh", "finish.sh.b64", "result",
               "original-logo.bin", "original-logo.next", "logo.next", "restore.next",
               "host-original.bin", "host-original.bin.b64")


def namespace_guard():
    return f'''for entry in {ROOT}/* {ROOT}/.[!.]* {ROOT}/..?*; do
    [ -e "$entry" ] || [ -L "$entry" ] || continue
    case "${{entry##*/}}" in {'|'.join(OWNED_NAMES)}) ;; *) exit 42;; esac
    [ ! -d "$entry" ] || exit 42
done
'''


def checked_hash(value):
    if not re.fullmatch("[0-9a-f]{64}", value):
        raise ValueError("invalid SHA-256")
    return value


def validate_logo(data):
    if len(data) != SIZE or not data.startswith(HEADER):
        raise ValueError("X2D II static Logo must match the reviewed 1024x768 format")
    return hashlib.sha256(data).hexdigest()


@dataclass(frozen=True)
class Baseline:
    device: str
    metadata: str
    label: str = LABEL

    def validate(self):
        checked_hash(self.device)
        if not re.fullmatch(r"600 0 0 1179701 [0-9]{1,11}", self.metadata) or self.label != LABEL:
            raise ValueError("baseline metadata outside reviewed profile")
        datetime.datetime.fromtimestamp(int(self.metadata.split()[-1]), datetime.timezone.utc)


def journal(baseline, images):
    baseline.validate()
    images = sorted(set(images))
    if not 1 <= len(images) <= 32:
        raise ValueError("image history outside bound")
    for value in images:
        checked_hash(value)
    return (json.dumps({"schema": 1, "backend": "x2dii-static-logo-v1", "firmware": "1.3.18.1",
                       "device": baseline.device, "originalHash": STOCK,
                       "originalMetadata": baseline.metadata, "originalLabel": LABEL,
                       "images": images}, sort_keys=True, separators=(",", ":")) + "\n").encode()


def header(baseline, journal_hash):
    baseline.validate()
    checked_hash(journal_hash)
    stamp = datetime.datetime.fromtimestamp(int(baseline.metadata.split()[-1]), datetime.timezone.utc).strftime("%Y%m%d%H%M.%S")
    return f'''#!/system/bin/sh
set -eu
export PATH=/system/bin:/system/xbin:/sbin
ROOT={ROOT}
LOGO={LOGO}
sha() {{ set -- $(sha256sum "$1"); printf '%s' "$1"; }}
hashok() {{ test "$(sha "$1")" = "$2"; }}
regular() {{ test -f "$1" && test ! -L "$1"; }}
attributes() {{
    chmod 600 "$1"
    chown 0:0 "$1"
    TZ=UTC toybox touch -m -t {stamp} "$1"
    test "$(stat -c '%a %u %g %s %Y' "$1")" = '{baseline.metadata}'
    test "$(ls -Z "$1")" = '{LABEL} '"$1"
}}
test "$(getprop ro.product.device)" = eagle2_hb722
hashok /factory_data/settings.ini {baseline.device}
hashok /system/bin/camera-gui {GUI}
test -d "$ROOT" && test ! -L "$ROOT"
regular "$ROOT/owner-id"
test "$(cat "$ROOT/owner-id")" = {baseline.device}
regular "$ROOT/journal.json"
hashok "$ROOT/journal.json" {journal_hash}
regular "$LOGO"
{namespace_guard()}
'''


def install(baseline, image_hash, previous_hash, record):
    for value in (image_hash, previous_hash):
        checked_hash(value)
    expected = journal(baseline, json.loads(record)["images"])
    if record != expected or image_hash not in json.loads(record)["images"]:
        raise ValueError("journal differs from immutable baseline or new image")
    admitted = {STOCK, *json.loads(record)["images"]}
    if previous_hash not in admitted:
        raise ValueError("previous image is not admitted by write-ahead journal")
    return header(baseline, hashlib.sha256(record).hexdigest()) + f'''
hashok "$LOGO" {previous_hash}
test "$(stat -c '%a %u %g %s %Y' "$LOGO")" = '{baseline.metadata}'
test "$(ls -Z "$LOGO")" = '{LABEL} '"$LOGO"
regular "$ROOT/image.bin"
hashok "$ROOT/image.bin" {image_hash}
test ! -L "$ROOT/original-logo.bin" && test ! -L "$ROOT/original-logo.next"
test ! -L "$ROOT/logo.next"
if [ ! -e "$ROOT/original-logo.bin" ]; then
    hashok "$LOGO" {STOCK}
    cp -p "$LOGO" "$ROOT/original-logo.next"
    hashok "$ROOT/original-logo.next" {STOCK}
    attributes "$ROOT/original-logo.next"
    sync
    mv -f "$ROOT/original-logo.next" "$ROOT/original-logo.bin"
fi
regular "$ROOT/original-logo.bin"
hashok "$ROOT/original-logo.bin" {STOCK}
cp -p "$ROOT/original-logo.bin" "$ROOT/logo.next"
cat "$ROOT/image.bin" > "$ROOT/logo.next"
hashok "$ROOT/logo.next" {image_hash}
attributes "$ROOT/logo.next"
sync
mv -f "$ROOT/logo.next" "$LOGO"
sync
hashok "$LOGO" {image_hash}
attributes "$LOGO"
hashok /factory_data/settings.ini {baseline.device}
hashok /system/bin/camera-gui {GUI}
printf 'HSS2_INSTALLED\\n'
'''


def restore(baseline, record):
    data = json.loads(record)
    expected = journal(baseline, data["images"])
    if record != expected:
        raise ValueError("journal differs from immutable baseline")
    admitted = "|".join([STOCK, *data["images"]])
    return header(baseline, hashlib.sha256(record).hexdigest()) + f'''
case "$(sha "$LOGO")" in {admitted}) ;; *) exit 41;; esac
test ! -L "$ROOT/restore.next"
if [ "$(sha "$LOGO")" != {STOCK} ]; then
    if regular "$ROOT/original-logo.bin" && hashok "$ROOT/original-logo.bin" {STOCK}; then
        cp -p "$ROOT/original-logo.bin" "$ROOT/restore.next"
    else
        regular "$ROOT/host-original.bin"
        hashok "$ROOT/host-original.bin" {STOCK}
        cp -p "$ROOT/host-original.bin" "$ROOT/restore.next"
    fi
    hashok "$ROOT/restore.next" {STOCK}
    attributes "$ROOT/restore.next"
    sync
    mv -f "$ROOT/restore.next" "$LOGO"
fi
hashok "$LOGO" {STOCK}
attributes "$LOGO"
sync
hashok /factory_data/settings.ini {baseline.device}
hashok /system/bin/camera-gui {GUI}
rm -f "$ROOT/image.bin" "$ROOT/image.bin.b64" "$ROOT/image.bin.gz"
rm -f "$ROOT/journal.json.b64" "$ROOT/install.sh.b64" "$ROOT/restore.sh.b64"
rm -f "$ROOT/finish.sh" "$ROOT/finish.sh.b64"
rm -f "$ROOT/journal.json.next.b64"
rm -f "$ROOT/host-original.bin" "$ROOT/host-original.bin.b64"
rm -f "$ROOT/logo.next" "$ROOT/restore.next" "$ROOT/original-logo.next"
rm -f "$ROOT/install.sh" "$ROOT/restore.sh" "$ROOT/result"
rm -f "$ROOT/original-logo.bin"
sync
rm -f "$ROOT/journal.json.next" "$ROOT/journal.json"
rm -f "$ROOT/owner-id"
rmdir "$ROOT"
printf 'HSS2_RESTORED\\n'
'''


def finish_cleanup(baseline):
    """Host fallback for preparation/final cleanup while target is original.

    Does not change the Logo or delete unknown entries. An unexpected entry
    keeps rmdir from succeeding and is reported for inspection.
    """
    baseline.validate()
    return f'''#!/system/bin/sh
set -eu
export PATH=/system/bin:/system/xbin:/sbin
test "$(getprop ro.product.device)" = eagle2_hb722
test "$(sha256sum /factory_data/settings.ini | cut -c1-64)" = {baseline.device}
test "$(sha256sum /system/bin/camera-gui | cut -c1-64)" = {GUI}
test "$(sha256sum {LOGO} | cut -c1-64)" = {STOCK}
test "$(stat -c '%a %u %g %s %Y' {LOGO})" = '{baseline.metadata}'
test "$(ls -Z {LOGO})" = '{LABEL} {LOGO}'
test -d {ROOT} && test ! -L {ROOT}
{namespace_guard()}
test ! -e {ROOT}/journal.json && test ! -L {ROOT}/journal.json
if [ -e {ROOT}/owner-id ] || [ -L {ROOT}/owner-id ]; then
    test -f {ROOT}/owner-id && test ! -L {ROOT}/owner-id
    test "$(cat {ROOT}/owner-id)" = {baseline.device}
    rm -f {ROOT}/image.bin {ROOT}/image.bin.b64 {ROOT}/image.bin.gz
    rm -f {ROOT}/install.sh {ROOT}/install.sh.b64 {ROOT}/restore.sh {ROOT}/restore.sh.b64
    rm -f {ROOT}/journal.json.next {ROOT}/journal.json.next.b64
    rm -f {ROOT}/host-original.bin {ROOT}/host-original.bin.b64
    rm -f {ROOT}/original-logo.bin {ROOT}/original-logo.next {ROOT}/logo.next {ROOT}/restore.next {ROOT}/result
    sync
    rm -f {ROOT}/owner-id
fi
rm -f {ROOT}/finish.sh.b64 {ROOT}/finish.sh
rmdir {ROOT}
printf 'HSS2_RESTORED\\n'
'''
