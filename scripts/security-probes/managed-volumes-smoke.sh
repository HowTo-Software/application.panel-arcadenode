#!/usr/bin/env bash
set -euo pipefail

node_image="${1:-arcadenode-files-check}"
root="${2:-/srv/arcadenode-quota/volumes}"
game_image="${3:-alpine:3.24}"
scratch="$(mktemp -d)"
node=''
read -r id < /proc/sys/kernel/random/uuid
read -r foreign_id < /proc/sys/kernel/random/uuid
read -r marker < /proc/sys/kernel/random/uuid
name="arcadenode-${id//-/}-data"
foreign_name="arcadenode-${foreign_id//-/}-data"

owns_volume() {
    [[ "$(docker volume inspect --format '{{index .Labels "arcadenode.files-probe"}}' "$name" 2>/dev/null)" == "$marker" ]]
}

game() {
    owns_volume || return 1
    docker run --rm --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
        --user 10001:10001 --env "FOREIGN_VOLUME=$foreign_name" \
        --mount "type=volume,source=$name,target=/data,volume-nocopy" "$game_image" sh -ec "$1"
}

clear_game_data() {
    game 'rm -f -- /data/server.properties /data/managed.cfg
          if [ -d "/data/$FOREIGN_VOLUME" ]; then rmdir -- "/data/$FOREIGN_VOLUME"; fi'
}

cleanup() {
    if [[ -n "$node" ]]; then docker rm -f "$node" >/dev/null || true; fi
    if owns_volume; then
        clear_game_data || true
        docker volume rm "$name" >/dev/null || true
    fi
    rm -rf -- "$scratch"
}
trap cleanup EXIT

for identity in node panel; do
    if [[ "$identity" == node ]]; then
        purpose_args=(-subj /CN=localhost -addext subjectAltName=DNS:localhost)
    else
        purpose_args=(-subj /CN=panel -addext extendedKeyUsage=clientAuth)
    fi
    openssl req -x509 -newkey rsa:2048 -nodes -days 1 "${purpose_args[@]}" \
        -keyout "$scratch/$identity.key" -out "$scratch/$identity.crt" 2>/dev/null
done
fingerprint="$(openssl x509 -in "$scratch/panel.crt" -outform DER | sha256sum | cut -d ' ' -f 1)"
cat > "$scratch/node.json" <<EOF
{"Files":{"Port":8443,"CertificatePath":"/tls/node.crt","KeyPath":"/tls/node.key",
"PanelCertificateSha256":["$fingerprint"],"ManagedVolumesDirectory":"/managed","BackupDirectory":"/backups","AllowUploads":true}}
EOF
chmod 644 "$scratch/node.key" "$scratch/node.crt" "$scratch/node.json"
node="$(docker run -d --user 10001:10001 --read-only --cap-drop ALL --security-opt no-new-privileges \
    --tmpfs /tmp:size=16m,mode=1777 --tmpfs /backups:size=8m,uid=10001,gid=10001,mode=0700 \
    --mount "type=bind,src=$scratch/node.json,dst=/app/appsettings.Production.json,readonly" \
    --mount "type=bind,src=$scratch/node.crt,dst=/tls/node.crt,readonly" \
    --mount "type=bind,src=$scratch/node.key,dst=/tls/node.key,readonly" \
    --mount "type=bind,src=$root,dst=/managed" -p 127.0.0.1::8443 "$node_image")"
port="$(docker port "$node" 8443/tcp | sed -n 's/^127\.0\.0\.1://p')"
origin="https://localhost:$port/v1/servers"
auth=(--cacert "$scratch/node.crt" --cert "$scratch/panel.crt" --key "$scratch/panel.key")
url="$origin/$id"

status() {
    curl --silent --show-error --max-time 8 "${auth[@]}" --output "$scratch/body" --write-out '%{http_code}' "$@"
}

expect() {
    local expected=$1 actual
    shift
    actual="$(status "$@")"
    if [[ "$actual" != "$expected" ]]; then
        echo "Expected $expected, got $actual." >&2
        return 1
    fi
}

for attempt in {1..18}; do
    if [[ "$(status "$url/files" 2>/dev/null)" == 404 ]]; then break; fi
    if [[ "$attempt" == 18 ]]; then docker logs "$node"; exit 1; fi
    sleep 1
done

create_volume() {
    docker volume create --driver arcadenode-quota --opt size=33554432 \
        --label "arcadenode.files-probe=$marker" "$name" >/dev/null
    owns_volume
}

create_volume
game 'printf "initial world" > /data/server.properties
      mkdir "/data/$FOREIGN_VOLUME"'
expect 200 "$url/files/download?path=server.properties"
test "$(cat "$scratch/body")" = 'initial world'
expect 404 "$origin/$foreign_id/files"
if curl --silent --fail --max-time 5 --cacert "$scratch/node.crt" "$url/files" >/dev/null; then
    echo 'The managed volume was accessible without the enrolled panel certificate.' >&2
    exit 1
fi
expect 201 -H 'Content-Type: application/octet-stream' --data-binary 'managed upload' "$url/files/upload?path=managed.cfg"
game 'test "$(cat /data/managed.cfg)" = "managed upload"'
expect 200 "$url/storage"
jq -e '.logicalBytes == 27 and .allocatedBytes > 0' "$scratch/body" >/dev/null
expect 200 -X POST "$url/backups/$marker"
jq -e '.size > 0 and (.sha256 | length) == 64' "$scratch/body" >/dev/null

clear_game_data
docker volume rm "$name" >/dev/null
create_volume
game 'printf "replacement world" > /data/server.properties'
expect 200 "$url/files/download?path=server.properties"
test "$(cat "$scratch/body")" = 'replacement world'
expect 200 "$url/storage"
jq -e '.logicalBytes == 17' "$scratch/body" >/dev/null
clear_game_data
docker volume rm "$name" >/dev/null
expect 404 "$url/files"
echo 'The file node discovered Docker-managed volumes, enforced node access, and refreshed replaced volumes.'
