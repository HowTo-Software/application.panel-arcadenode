#!/bin/bash
set -euo pipefail
umask 027

version="${1:?Minecraft version is required}"
requested_build="${2:?Paper build is required}"
destination="${3:?Server JAR path is required}"
service=https://fill.papermc.io/v3/projects/paper
user_agent='ArcadeNode/2.0 (https://github.com/HowTo-Software/application.panel-arcadenode)'
fetch=(curl --disable -fsS --proto '=https' --connect-timeout 10 --max-time 120 --user-agent "$user_agent")

if [[ "$requested_build" != latest && ! "$requested_build" =~ ^[1-9][0-9]{0,8}$ ]]; then
    echo '[ERROR] PAPER_BUILD must be latest or a positive build number.' >&2
    exit 1
fi
if [[ "$version" == latest ]]; then
    version="$("${fetch[@]}" "$service" | jq -er '
        first(.versions | to_entries[] | .value[] | select(test("^[0-9]+(\\.[0-9]+){1,3}$")))
    ')"
fi
if [[ ! "$version" =~ ^([1-9][0-9]{0,2})(\.[0-9]{1,3}){1,3}$ ]]; then
    echo '[ERROR] Use a Paper release version or latest.' >&2
    exit 1
fi
java_runtime=21
if (( BASH_REMATCH[1] >= 26 )); then java_runtime=25; fi

if ! artifact="$("${fetch[@]}" --max-filesize 16777216 "$service/versions/$version/builds" | jq -ce --arg requested "$requested_build" '
    map(select(.channel == "STABLE" and
        ($requested == "latest" or (.id | tostring) == $requested))) |
    max_by(.id) | select(. != null) |
    {id, download: .downloads."server:default"}
')"; then
    echo "[ERROR] No stable Paper build matches version $version and build $requested_build." >&2
    exit 1
fi
build="$(printf '%s\n' "$artifact" | jq -er '.id | numbers')"
download_url="$(printf '%s\n' "$artifact" | jq -er '.download.url | strings')"
checksum="$(printf '%s\n' "$artifact" | jq -er '.download.checksums.sha256 | strings')"
if [[ "$download_url" != https://fill-data.papermc.io/* || ! "$checksum" =~ ^[0-9a-f]{64}$ ]]; then
    echo '[ERROR] Paper metadata did not contain an approved download host and SHA-256.' >&2
    exit 1
fi

jar_tmp="$(mktemp "${destination}.download.XXXXXX")"
runtime_tmp=''
trap 'rm -f -- "$jar_tmp" "$runtime_tmp"' EXIT
runtime_tmp="$(mktemp "${destination}.runtime.XXXXXX")"
"${fetch[@]}" --max-filesize 268435456 --output "$jar_tmp" "$download_url"
if ! printf '%s  %s\n' "$checksum" "$jar_tmp" | sha256sum -c - >/dev/null; then
    echo '[ERROR] The Paper server JAR does not match the published SHA-256.' >&2
    exit 1
fi
printf '%s\n' "$java_runtime" > "$runtime_tmp"
mv -f -- "$runtime_tmp" "${destination}.java-runtime"
mv -f -- "$jar_tmp" "$destination"
echo "[INFO] Installed Paper $version build $build for Java $java_runtime."
