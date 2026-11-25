#!/bin/bash
set -euo pipefail

scratch="$(mktemp -d)"
trap 'rm -rf -- "$scratch"' EXIT
printf 'Synthetic Minecraft server JAR\n' > "$scratch/upstream.jar"
export fixture_jar="$scratch/upstream.jar"
fixture_checksum="$(sha1sum "$fixture_jar" | cut -d ' ' -f 1)"
export fixture_checksum

curl() {
    [[ "$1" == --disable ]] || return 1
    local output='' url=''
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --output) output="$2"; shift ;;
            https://*) url="$1" ;;
        esac
        shift
    done
    case "$url" in
        https://piston-meta.mojang.com/mc/game/version_manifest_v2.json)
            printf '%s\n' '{"latest":{"release":"26.1"},"versions":[{"id":"1.21.1","url":"https://piston-meta.mojang.com/java21"},{"id":"26.1","url":"https://piston-meta.mojang.com/java25"}]}'
            ;;
        https://piston-meta.mojang.com/java*)
            local checksum="$fixture_checksum" server_url='https://piston-data.mojang.com/server.jar'
            if [ "${INVALID_JAR:-0}" -eq 1 ]; then checksum='0123456789012345678901234567890123456789'; fi
            if [ "${UNKNOWN_HOST:-0}" -eq 1 ]; then server_url='https://outside.example.test/server.jar'; fi
            printf '{"javaVersion":{"majorVersion":%s},"downloads":{"server":{"url":"%s","sha1":"%s"}}}\n' \
                "${url##*java}" "$server_url" "$checksum"
            ;;
        https://piston-data.mojang.com/server.jar) cp "$fixture_jar" "$output" ;;
        *) return 1 ;;
    esac
}
export -f curl

download='game-servers/minecraft/download-vanilla.sh'
bash "$download" 'latest' "$scratch/latest-server.jar"
cmp "$fixture_jar" "$scratch/latest-server.jar"
test "$(cat "$scratch/latest-server.jar.java-runtime")" = 25
bash "$download" '1.21.1' "$scratch/pinned.jar"
cmp "$fixture_jar" "$scratch/pinned.jar"
test "$(cat "$scratch/pinned.jar.java-runtime")" = 21

if INVALID_JAR=1 bash "$download" 'latest' "$scratch/unverified.jar"; then
    echo 'Wrong checksum was accepted.' >&2
    exit 1
fi
test ! -e "$scratch/unverified.jar"
test ! -e "$scratch/unverified.jar.java-runtime"
test -z "$(find "$scratch" -maxdepth 1 -name 'unverified.jar.*' -print -quit)"

if UNKNOWN_HOST=1 bash "$download" 'latest' "$scratch/unknown-host.jar"; then
    echo 'Unexpected download host was accepted.' >&2
    exit 1
fi
test ! -e "$scratch/unknown-host.jar"
if bash "$download" 'missing' "$scratch/missing.jar"; then
    echo 'An unknown Minecraft release was accepted.' >&2
    exit 1
fi
echo 'Vanilla download smoke passed.'
