#!/bin/bash
set -euo pipefail

scratch="$(mktemp -d)"
trap 'rm -rf -- "$scratch"' EXIT
export fixture_jar="$scratch/paper.jar"
printf 'Synthetic Paper server JAR\n' > "$fixture_jar"
fixture_checksum="$(sha256sum "$fixture_jar" | cut -d ' ' -f 1)"
export fixture_checksum

curl() {
    [[ "$1" == --disable ]] || return 1
    local output='' url='' agent=''
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --output) output="$2"; shift ;;
            --user-agent) agent="$2"; shift ;;
            https://*) url="$1" ;;
        esac
        shift
    done
    [[ "$agent" == ArcadeNode/* ]] || return 1
    case "$url" in
        https://fill.papermc.io/v3/projects/paper)
            printf '%s\n' '{"versions":{"26.2":["26.2-rc-3","26.2"],"1.21":["1.21.1"]}}'
            ;;
        https://fill.papermc.io/v3/projects/paper/versions/*/builds)
            local checksum="$fixture_checksum" artifact='https://fill-data.papermc.io/v1/objects/paper.jar'
            if [[ "${INVALID_JAR:-0}" == 1 ]]; then
                checksum='0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
            fi
            if [[ "${UNKNOWN_HOST:-0}" == 1 ]]; then artifact='https://outside.example.test/paper.jar'; fi
            jq -cn --arg url "$artifact" --arg hash "$checksum" '
                def download($url): {"server:default": {url: $url, checksums: {sha256: $hash}}};
                [{id: 1, channel: "STABLE", downloads: download($url)},
                 {id: 6, channel: "EXPERIMENTAL", downloads: download("https://outside.example.test/wrong.jar")},
                 {id: 3, channel: "STABLE", downloads: download($url)}]'
            ;;
        https://fill-data.papermc.io/v1/objects/paper.jar) cp "$fixture_jar" "$output" ;;
        *) return 1 ;;
    esac
}
export -f curl

download=game-servers/minecraft/download-paper.sh
output="$(bash "$download" latest latest "$scratch/latest.jar")"
[[ "$output" == *'26.2 build 3 for Java 25'* ]]
cmp "$fixture_jar" "$scratch/latest.jar"
test "$(cat "$scratch/latest.jar.java-runtime")" = 25
output="$(bash "$download" 1.21.1 1 "$scratch/pinned.jar")"
[[ "$output" == *'1.21.1 build 1 for Java 21'* ]]
cmp "$fixture_jar" "$scratch/pinned.jar"
test "$(cat "$scratch/pinned.jar.java-runtime")" = 21

if INVALID_JAR=1 bash "$download" latest latest "$scratch/unverified.jar"; then
    echo 'Wrong Paper checksum was accepted.' >&2
    exit 1
fi
if UNKNOWN_HOST=1 bash "$download" 1.21.1 latest "$scratch/unknown-host.jar"; then
    echo 'Unexpected Paper download host was accepted.' >&2
    exit 1
fi
if bash "$download" 1.21.1 6 "$scratch/experimental.jar"; then
    echo 'An experimental build was selected.' >&2
    exit 1
fi
test -z "$(find "$scratch" -maxdepth 1 \( -name 'unverified.jar*' -o -name 'unknown-host.jar*' -o -name 'experimental.jar*' \) -print -quit)"
echo 'Paper download smoke passed.'
