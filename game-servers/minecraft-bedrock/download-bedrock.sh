#!/bin/bash
set -euo pipefail
umask 027

requested="${1:?Bedrock release is required}"
server="${2:?Bedrock server directory is required}"
if [[ "$server" != /* || ! -d "$server" || -L "$server" ]]; then exit 1; fi

catalog='https://net-secondary.web.minecraft-services.net/api/v1.0/download/links'
distribution='https://www.minecraft.net/bedrockdedicatedserver/bin-linux'
request=(curl --disable --fail --silent --show-error --proto '=https' --http1.1 --user-agent 'Mozilla/5.0' --connect-timeout 10)
version_format='^[0-9]{1,4}(\.[0-9]{1,4}){3}$'
pending="$server/.bedrock-update-pending"
current=''
if [[ -f "$server/.version" && ! -L "$server/.version" ]]; then
    IFS= read -r current < "$server/.version" || true
fi
if [[ -L "$pending" || ( -e "$pending" && ! -d "$pending" ) ]]; then exit 1; fi

running_installed() {
    [[ "$current" =~ $version_format && -f "$server/bedrock_server" && -x "$server/bedrock_server" &&
        ! -L "$server/bedrock_server" && ! -d "$pending" ]]
}

unavailable() {
    if [[ "${requested^^}" == LATEST ]] && running_installed; then
        echo "[INFO] Official Bedrock update unavailable. Starting installed release $current."
        return 0
    fi
    echo '[ERROR] Could not install the requested official Bedrock release.' >&2
    return 1
}

if [[ "${requested^^}" == LATEST ]]; then
    if ! manifest="$("${request[@]}" --max-time 30 --max-filesize 1048576 "$catalog")" ||
        ! url="$(jq -er '[.result.links[] | select(.downloadType == "serverBedrockLinux") | .downloadUrl] |
            select(length == 1) | .[0] | select(type == "string")' <<< "$manifest")"; then
        unavailable
        exit
    fi
    release="${url##*/bedrock-server-}"
    release="${release%.zip}"
    if [[ ! "$release" =~ $version_format || "$url" != "$distribution/bedrock-server-$release.zip" ]]; then
        unavailable
        exit
    fi
else
    if [[ ! "$requested" =~ $version_format ]]; then
        echo '[ERROR] Use LATEST or a four-part Bedrock release number.' >&2
        exit 1
    fi
    release="$requested"
    url="$distribution/bedrock-server-$release.zip"
fi

if [[ "$current" == "$release" ]] && running_installed; then exit 0; fi

stage="$(mktemp -d -- "$server/.bedrock-install.XXXXXX")"
trap 'rm -rf -- "$stage"' EXIT
package="$stage/release"
mkdir "$package"
echo "[INFO] Downloading official Bedrock release $release."
if ! "${request[@]}" --max-time 900 --speed-limit 16384 --speed-time 60 --max-filesize 536870912 \
    --output "$stage/server.zip" "$url" ||
    ! unzip -q "$stage/server.zip" -d "$package" ||
    [[ ! -f "$package/bedrock_server" || ! -f "$package/server.properties" ]] ||
    [[ -n "$(find "$package" -type l -print -quit)" ]] ||
    [[ "$(od -An -N4 -tx1 "$package/bedrock_server" | tr -d ' \n')" != 7f454c46 ]]; then
    unavailable
    exit
fi

chmod 0750 "$package/bedrock_server"
if [[ ! -d "$pending" ]]; then mkdir "$pending"; fi
for asset in "$package"/*; do
    name="${asset##*/}"
    case "$name" in
        bedrock_server|server.properties|allowlist.json|whitelist.json|permissions.json|worlds|logs) continue ;;
    esac
    destination="$server/$name"
    if [[ -L "$destination" ]]; then
        case "$name" in
            resource_packs|behavior_packs)
                data="${server%/*}/data"
                link="$(readlink -- "$destination")"
                if [[ ( "$link" != "$data/$name" && "$link" != "../data/$name" ) ||
                    -L "$data" || ! -d "$data/$name" || -L "$data/$name" ]]; then exit 1; fi
                destination="$data/$name"
                ;;
            *) exit 1 ;;
        esac
    fi
    cp -aT --remove-destination -- "$asset" "$destination"
done
mv -fT -- "$package/bedrock_server" "$server/bedrock_server"
printf '%s\n' "$release" > "$stage/version"
mv -fT -- "$stage/version" "$server/.version"
rmdir "$pending"
echo "[INFO] Installed official Bedrock release $release."
