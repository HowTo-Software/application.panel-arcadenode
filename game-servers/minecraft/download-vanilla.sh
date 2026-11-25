#!/bin/bash
set -euo pipefail
umask 027

version="${1:?Minecraft version is required}"
destination="${2:?Server JAR path is required}"
fetch=(curl --disable -fsS --proto '=https' --connect-timeout 10 --max-time 120)

manifest="$("${fetch[@]}" https://piston-meta.mojang.com/mc/game/version_manifest_v2.json)"
release="$(printf '%s\n' "$manifest" | jq -er --arg version "$version" 'if $version == "latest" then .latest.release else $version end')"
version_url="$(printf '%s\n' "$manifest" | jq -er --arg release "$release" 'first(.versions[] | select(.id == $release) | .url)')"
case "$version_url" in
    https://piston-meta.mojang.com/*|https://launchermeta.mojang.com/*) ;;
    *) echo '[ERROR] Unexpected Mojang metadata host.' >&2; exit 1 ;;
esac

metadata="$("${fetch[@]}" "$version_url")"
server_url="$(printf '%s\n' "$metadata" | jq -er '.downloads.server.url')"
checksum="$(printf '%s\n' "$metadata" | jq -er '.downloads.server.sha1')"
major="$(printf '%s\n' "$metadata" | jq -er '.javaVersion.majorVersion // 8')"
case "$server_url" in
    https://piston-data.mojang.com/*|https://launcher.mojang.com/*) ;;
    *) echo '[ERROR] Unexpected Mojang server download host.' >&2; exit 1 ;;
esac
if [[ ! "$checksum" =~ ^[0-9a-f]{40}$ || ! "$major" =~ ^[0-9]{1,2}$ ]] || (( major < 8 || major > 25 )); then
    echo '[ERROR] Unsupported Java runtime or missing server checksum in the Mojang metadata.' >&2
    exit 1
fi

java_runtime=21
if (( major > 21 )); then java_runtime=25; fi
echo "[INFO] Downloading Vanilla $release for Java $java_runtime."
jar_tmp="$(mktemp "${destination}.download.XXXXXX")"
runtime_tmp=''
trap 'rm -f -- "$jar_tmp" "$runtime_tmp"' EXIT
runtime_tmp="$(mktemp "${destination}.runtime.XXXXXX")"
"${fetch[@]}" --output "$jar_tmp" "$server_url"
if ! printf '%s  %s\n' "$checksum" "$jar_tmp" | sha1sum -c - >/dev/null; then
    echo '[ERROR] The Minecraft server JAR checksum differs from the Mojang metadata.' >&2
    exit 1
fi
printf '%s\n' "$java_runtime" > "$runtime_tmp"
mv -f -- "$runtime_tmp" "${destination}.java-runtime"
mv -f -- "$jar_tmp" "$destination"
