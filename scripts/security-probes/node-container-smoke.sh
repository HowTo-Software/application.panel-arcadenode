#!/usr/bin/env bash
set -euo pipefail

image="${1:-arcadenode-files-check}"
scratch="$(mktemp -d)"
container=''
legacy_container=''
cleanup() {
    if [[ -n "$container" ]]; then docker rm -f "$container" >/dev/null || true; fi
    if [[ -n "$legacy_container" ]]; then docker rm -f "$legacy_container" >/dev/null || true; fi
    rm -rf -- "$scratch"
}
trap cleanup EXIT

server_id='853ac87a-d96d-40c0-a155-48fbcc4bbcc3'
foreign_id='fb7f9d68-563d-4e02-8536-0e7815dd2ef5'
mkdir "$scratch/keys" "$scratch/game" "$scratch/backups" "$scratch/recovery"
chmod 700 "$scratch/keys"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=localhost -addext subjectAltName=DNS:localhost \
    -keyout "$scratch/keys/node.key" -out "$scratch/keys/node.crt" 2>/dev/null
for caller in panel other; do
    openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=$caller" -addext extendedKeyUsage=clientAuth \
        -keyout "$scratch/keys/$caller.key" -out "$scratch/keys/$caller.crt" 2>/dev/null
done
fingerprint="$(openssl x509 -in "$scratch/keys/panel.crt" -outform DER | sha256sum | cut -d ' ' -f 1)"
printf '%s\n' 'motd=ArcadeNode' > "$scratch/game/server.properties"
ln -s /etc/passwd "$scratch/game/outside-link"
cat > "$scratch/node.json" <<EOF
{"Files":{"Port":8443,"CertificatePath":"/tls/node.crt","KeyPath":"/tls/node.key","BackupDirectory":"/backups","RestoreDirectory":"/recovery",
"PanelCertificateSha256":["$fingerprint"],"AllowUploads":true,"Volumes":{"$server_id":"/game"}}}
EOF

container="$(docker run -d --user "$(id -u):$(id -g)" --read-only --cap-drop ALL --security-opt no-new-privileges \
    --tmpfs /tmp:size=16m,mode=1777 \
    --mount "type=bind,src=$scratch/node.json,dst=/app/appsettings.Production.json,readonly" \
    --mount "type=bind,src=$scratch/keys/node.crt,dst=/tls/node.crt,readonly" \
    --mount "type=bind,src=$scratch/keys/node.key,dst=/tls/node.key,readonly" \
    --mount "type=bind,src=$scratch/game,dst=/game" \
    --mount "type=bind,src=$scratch/backups,dst=/backups" \
    --mount "type=bind,src=$scratch/recovery,dst=/recovery" \
    -p 127.0.0.1::8443 "$image")"
host_port="$(docker port "$container" 8443/tcp | sed -n 's/^127\.0\.0\.1://p')"
origin="https://localhost:$host_port"
auth=(--cacert "$scratch/keys/node.crt" --cert "$scratch/keys/panel.crt" --key "$scratch/keys/panel.key")
list="$origin/v1/servers/$server_id/files"

for attempt in {1..15}; do
    if curl --silent --fail --max-time 3 "${auth[@]}" "$list" -o "$scratch/list.json"; then break; fi
    if [[ "$attempt" == 15 ]]; then docker logs "$container"; exit 1; fi
    sleep 1
done
jq -e '(.entries | map(.name)) == ["server.properties"] and (.truncated == false)' "$scratch/list.json" >/dev/null

if curl --silent --fail --max-time 5 --cacert "$scratch/keys/node.crt" "$list" >/dev/null; then
    echo 'Node accepted a request without a client certificate.' >&2
    exit 1
fi
if curl --silent --fail --max-time 5 --cacert "$scratch/keys/node.crt" \
    --cert "$scratch/keys/other.crt" --key "$scratch/keys/other.key" "$list" >/dev/null; then
    echo 'Node accepted an unapproved client certificate.' >&2
    exit 1
fi

expect_status() {
    local expected="$1"
    shift
    local actual
    actual="$(curl --silent --show-error --max-time 8 "${auth[@]}" --output "$scratch/body" --write-out '%{http_code}' "$@")"
    if [[ "$actual" != "$expected" ]]; then
        echo "Expected $expected, got $actual." >&2
        return 1
    fi
}
expect_status 200 "$list/download?path=server.properties"
cmp "$scratch/body" "$scratch/game/server.properties"
expect_status 404 "$list/download?path=outside-link"
expect_status 404 "$origin/v1/servers/$foreign_id/files"
expect_status 200 "$origin/v1/servers/$server_id/storage"
length="$(wc -c < "$scratch/game/server.properties")"
jq -e --argjson size "$length" '.logicalBytes == $size and .allocatedBytes >= 0' "$scratch/body" >/dev/null
expect_status 201 -H 'Content-Type: application/octet-stream' --data-binary 'saved-data' "$list/upload?path=new.cfg"
expect_status 409 -H 'Content-Type: application/octet-stream' --data-binary 'replacement' "$list/upload?path=new.cfg"
test "$(cat "$scratch/game/new.cfg")" = 'saved-data'
checksum="$(sha256sum "$scratch/game/new.cfg" | cut -d ' ' -f 1)"
edit_payload="$(jq -nc --arg sha "$checksum" '{content:"edited-data", sha256:$sha}')"
expect_status 204 -X PUT -H 'Content-Type: application/json' --data-binary "$edit_payload" "$list/text?path=new.cfg"
expect_status 412 -X PUT -H 'Content-Type: application/json' --data-binary "$edit_payload" "$list/text?path=new.cfg"
test "$(cat "$scratch/game/new.cfg")" = 'edited-data'
chmod 640 "$scratch/game/server.properties" "$scratch/game/new.cfg"
expect_status 400 -H 'Content-Type: application/json' --data-binary '{"entries":["new.cfg"],"mode":"4750"}' "$list/chmod"
expect_status 404 -H 'Content-Type: application/json' --data-binary '{"entries":["new.cfg","outside-link"],"mode":"755"}' "$list/chmod"
test "$(stat -c %a "$scratch/game/new.cfg")" = 640
ln -- "$scratch/game/new.cfg" "$scratch/game/hard-linked.cfg"
expect_status 422 -H 'Content-Type: application/json' --data-binary '{"entries":["server.properties","hard-linked.cfg"],"mode":"755"}' "$list/chmod"
test "$(stat -c %a "$scratch/game/server.properties")" = 640
rm -- "$scratch/game/hard-linked.cfg"
expect_status 204 -H 'Content-Type: application/json' --data-binary '{"entries":["server.properties","new.cfg"],"mode":"0750"}' "$list/chmod"
expect_status 200 "$list"
jq -e '(.entries | sort_by(.name) | map([.name, .mode])) == [["new.cfg","750"],["server.properties","750"]]' "$scratch/body" >/dev/null
test "$(stat -c %a "$scratch/game/server.properties")" = 750
test "$(stat -c %a "$scratch/game/new.cfg")" = 750
backup_id='7e968f1d-fc48-48ed-8b1f-2bff64823abb'
backup_url="$origin/v1/servers/$server_id/backups/$backup_id"
expect_status 422 -X POST "$backup_url"
archive="$scratch/backups/${server_id//-/}/${backup_id//-/}.tar.gz"
test ! -e "$archive"
rm -- "$scratch/game/outside-link"
mkdir "$scratch/game/world"
printf 'world save data' > "$scratch/game/world/save.dat"
chmod 750 "$scratch/game/world"
chmod 740 "$scratch/game/world/save.dat"
touch -d '@1760000000' "$scratch/game/world" "$scratch/game/world/save.dat"
expect_status 200 -X POST "$backup_url"
checksum="$(sha256sum "$archive" | cut -d ' ' -f 1)"
jq -e --arg sha "${checksum^^}" '.sha256 == $sha and .size > 0' "$scratch/body" >/dev/null
test "$(tar -xOzf "$archive" new.cfg)" = 'edited-data'
expect_status 200 -X POST "$backup_url"
test "$(sha256sum "$archive" | cut -d ' ' -f 1)" = "$checksum"
expect_status 200 "$backup_url"
cmp "$scratch/body" "$archive"
expect_status 404 "$origin/v1/servers/$foreign_id/backups/$backup_id"
dotnet run --project scripts/pterodactyl-migration/Preflight.csproj -- restore-backup "$server_id" "$archive" "$checksum" "$scratch/recovered" > "$scratch/restore.json"
jq -e --arg sid "$server_id" '.mode == "restore_backup" and .server_id == $sid and .files == 3 and .directories == 1' "$scratch/restore.json" >/dev/null
cmp "$scratch/game/world/save.dat" "$scratch/recovered/world/save.dat"
cmp "$scratch/game/new.cfg" "$scratch/recovered/new.cfg"
test "$(stat -c '%a:%Y' "$scratch/game/world")" = "$(stat -c '%a:%Y' "$scratch/recovered/world")"
test "$(stat -c '%a:%Y' "$scratch/game/world/save.dat")" = "$(stat -c '%a:%Y' "$scratch/recovered/world/save.dat")"
restore_url="$backup_url/restore"
restore_body="$(jq -nc --arg sha "$checksum" '{sha256:$sha}')"
wrong_restore_body="$(jq -nc --arg sha "$(printf '%064d' 0)" '{sha256:$sha}')"
bundle="$scratch/recovery/${server_id//-/}/${backup_id//-/}"
expect_status 422 -H 'Content-Type: application/json' --data-binary "$wrong_restore_body" "$restore_url"
test ! -e "$bundle"
expect_status 404 -H 'Content-Type: application/json' --data-binary "$restore_body" "$origin/v1/servers/$foreign_id/backups/$backup_id/restore"
expect_status 200 -H 'Content-Type: application/json' --data-binary "$restore_body" "$restore_url"
jq -e --arg sha "${checksum^^}" '.archiveSha256 == $sha and .files == 3 and .directories == 1' "$scratch/body" >/dev/null
cmp "$scratch/game/world/save.dat" "$bundle/data/world/save.dat"
printf 'new live data' > "$scratch/game/world/save.dat"
expect_status 200 -H 'Content-Type: application/json' --data-binary "$restore_body" "$restore_url"
test "$(cat "$bundle/data/world/save.dat")" = 'world save data'
test "$(cat "$scratch/game/world/save.dat")" = 'new live data'
expect_status 422 -H 'Content-Type: application/json' --data-binary "$wrong_restore_body" "$restore_url"

mkdir "$scratch/game/world/cache"
printf 'legacy default exclusion' > "$scratch/game/world/private.key"
printf 'cache entry' > "$scratch/game/world/cache/log.txt"
printf 'discard temporary' > "$scratch/game/old.tmp"
printf 'keep temporary' > "$scratch/game/world/keep.tmp"
printf '%s\n' '/world/private.key' 'cache/' '*.tmp' '!world/keep.tmp' > "$scratch/game/.pteroignore"
filtered_id='7e968f1d-fc48-48ed-8b1f-2bff64823abc'
filtered_url="$origin/v1/servers/$server_id/backups/$filtered_id/with-exclusions"
expect_status 200 -X POST "$filtered_url"
filtered="$scratch/backups/${server_id//-/}/${filtered_id//-/}.tar.gz"
tar -tzf "$filtered" > "$scratch/filtered-files.txt"
grep -Fx 'world/keep.tmp' "$scratch/filtered-files.txt" >/dev/null
grep -Fx 'world/save.dat' "$scratch/filtered-files.txt" >/dev/null
if grep -Eq '^(world/private\.key|world/cache(/|$)|old\.tmp$)' "$scratch/filtered-files.txt"; then
    echo 'The default backup included an excluded file.' >&2
    exit 1
fi
explicit_id='7e968f1d-fc48-48ed-8b1f-2bff64823abd'
explicit_url="$origin/v1/servers/$server_id/backups/$explicit_id/with-exclusions"
expect_status 200 -H 'Content-Type: text/plain' --data-binary '/world/save.dat' "$explicit_url"
explicit="$scratch/backups/${server_id//-/}/${explicit_id//-/}.tar.gz"
tar -tzf "$explicit" > "$scratch/explicit-files.txt"
grep -Fx 'world/private.key' "$scratch/explicit-files.txt" >/dev/null
if grep -Fx 'world/save.dat' "$scratch/explicit-files.txt" >/dev/null; then
    echo 'The backup ignored its explicit rule.' >&2
    exit 1
fi
invalid_id='7e968f1d-fc48-48ed-8b1f-2bff64823abe'
printf '%s\n' 'bad[' > "$scratch/game/.pteroignore"
expect_status 422 -X POST "$origin/v1/servers/$server_id/backups/$invalid_id/with-exclusions"
test ! -e "$scratch/backups/${server_id//-/}/${invalid_id//-/}.tar.gz"

delete_url="$origin/v1/servers/$server_id/backups/$explicit_id"
expect_status 404 -X DELETE "$origin/v1/servers/$foreign_id/backups/$explicit_id"
test -f "$explicit"
expect_status 204 -X DELETE "$delete_url"
test ! -e "$explicit"
expect_status 204 -X DELETE "$delete_url"
expect_status 404 "$delete_url"
ln -s "${filtered##*/}" "$explicit"
expect_status 422 -X DELETE "$delete_url"
test -L "$explicit"
test -f "$filtered"

expect_status 201 -H 'Content-Type: application/json' --data-binary '{"path":"configs"}' "$list/directories"
test -d "$scratch/game/configs"
expect_status 409 -H 'Content-Type: application/json' --data-binary '{"path":"configs"}' "$list/directories"
expect_status 400 -H 'Content-Type: application/json' --data-binary '{"path":"../outside"}' "$list/directories"
ln -s /tls "$scratch/game/shortcut"
expect_status 404 -H 'Content-Type: application/json' --data-binary '{"path":"shortcut/new"}' "$list/directories"
expect_status 404 -H 'Content-Type: application/json' --data-binary '{"source":"shortcut","destination":"moved-link"}' "$list/move"
expect_status 404 -H 'Content-Type: application/json' --data-binary '{"source":"new.cfg","destination":"shortcut/escaped.cfg"}' "$list/move"
expect_status 404 -H 'Content-Type: application/json' --data-binary '{"path":"foreign"}' "$origin/v1/servers/$foreign_id/files/directories"
expect_status 204 -H 'Content-Type: application/json' --data-binary '{"source":"new.cfg","destination":"configs/game.cfg"}' "$list/move"
test ! -e "$scratch/game/new.cfg"
test "$(cat "$scratch/game/configs/game.cfg")" = 'edited-data'
expect_status 409 -H 'Content-Type: application/json' --data-binary '{"source":"configs/game.cfg","destination":"server.properties"}' "$list/move"
test "$(cat "$scratch/game/server.properties")" = 'motd=ArcadeNode'
expect_status 400 -H 'Content-Type: application/json' --data-binary '{"source":"configs","destination":"configs/nested"}' "$list/move"
expect_status 201 -H 'Content-Type: application/json' --data-binary '{"path":"occupied"}' "$list/directories"
expect_status 409 -H 'Content-Type: application/json' --data-binary '{"source":"configs","destination":"occupied"}' "$list/move"
expect_status 204 -H 'Content-Type: application/json' --data-binary '{"source":"configs","destination":"moved-configs"}' "$list/move"
test ! -e "$scratch/game/configs"
test "$(cat "$scratch/game/moved-configs/game.cfg")" = 'edited-data'

head -c 196608 /dev/urandom > "$scratch/game/moved-configs/source.bin"
chmod 750 "$scratch/game/moved-configs/source.bin"
copy_body='{"source":"moved-configs/source.bin","destination":"occupied/copy.bin"}'
expect_status 404 -H 'Content-Type: application/json' --data-binary "$copy_body" "$origin/v1/servers/$foreign_id/files/copy"
expect_status 201 -H 'Content-Type: application/json' --data-binary "$copy_body" "$list/copy"
cmp "$scratch/game/moved-configs/source.bin" "$scratch/game/occupied/copy.bin"
test "$(stat -c %a "$scratch/game/occupied/copy.bin")" = 750
expect_status 409 -H 'Content-Type: application/json' --data-binary "$copy_body" "$list/copy"
expect_status 409 -H 'Content-Type: application/json' --data-binary '{"source":"server.properties","destination":"occupied/copy.bin"}' "$list/copy"
cmp "$scratch/game/moved-configs/source.bin" "$scratch/game/occupied/copy.bin"
expect_status 404 -H 'Content-Type: application/json' --data-binary '{"source":"outside-link","destination":"escaped.bin"}' "$list/copy"
expect_status 404 -H 'Content-Type: application/json' --data-binary '{"source":"server.properties","destination":"shortcut/escaped.bin"}' "$list/copy"
expect_status 422 -H 'Content-Type: application/json' --data-binary '{"source":"moved-configs","destination":"not-a-directory"}' "$list/copy"
ln "$scratch/game/moved-configs/source.bin" "$scratch/game/source-link.bin"
expect_status 422 -H 'Content-Type: application/json' --data-binary '{"source":"source-link.bin","destination":"blocked.bin"}' "$list/copy"
truncate -s 536870913 "$scratch/game/oversized.bin"
expect_status 413 -H 'Content-Type: application/json' --data-binary '{"source":"oversized.bin","destination":"over-limit.bin"}' "$list/copy"
test ! -e "$scratch/game/escaped.bin"
test ! -e "$scratch/game/not-a-directory"
test ! -e "$scratch/game/blocked.bin"
test ! -e "$scratch/game/over-limit.bin"

mkdir -p "$scratch/game/selection/world/empty"
printf 'file outside selection' > "$scratch/game/selection/ignored.txt"
printf 'configuration' > "$scratch/game/selection/game.cfg"
printf 'world data' > "$scratch/game/selection/world/save.dat"
compress_body='{"directory":"selection","entries":["game.cfg","world"],"destination":"selected.tar.gz"}'
expect_status 404 -H 'Content-Type: application/json' --data-binary "$compress_body" "$origin/v1/servers/$foreign_id/files/compress"
expect_status 201 -H 'Content-Type: application/json' --data-binary "$compress_body" "$list/compress"
selected="$scratch/game/selection/selected.tar.gz"
tar -tzf "$selected" | sed 's|/$||' | sort > "$scratch/selection.list"
printf '%s\n' 'game.cfg' 'world' 'world/empty' 'world/save.dat' > "$scratch/expected-selection.list"
diff -u "$scratch/expected-selection.list" "$scratch/selection.list"
test "$(tar -xOzf "$selected" world/save.dat)" = 'world data'
checksum_before="$(sha256sum < "$selected")"
expect_status 409 -H 'Content-Type: application/json' --data-binary "$compress_body" "$list/compress"
test "$(sha256sum < "$selected")" = "$checksum_before"
ln -s /tls "$scratch/game/selection/world/linked"
expect_status 422 -H 'Content-Type: application/json' --data-binary '{"directory":"selection","entries":["world"],"destination":"blocked.tar.gz"}' "$list/compress"
test ! -e "$scratch/game/selection/blocked.tar.gz"
expect_status 413 -H 'Content-Type: application/json' --data-binary '{"entries":["oversized.bin"],"destination":"big.tar.gz"}' "$list/compress"
test ! -e "$scratch/game/big.tar.gz"

extract_body='{"source":"selection/selected.tar.gz","destination":"selection/unpacked"}'
expect_status 404 -H 'Content-Type: application/json' --data-binary "$extract_body" "$origin/v1/servers/$foreign_id/files/extract"
expect_status 201 -H 'Content-Type: application/json' --data-binary "$extract_body" "$list/extract"
cmp "$scratch/game/selection/game.cfg" "$scratch/game/selection/unpacked/game.cfg"
cmp "$scratch/game/selection/world/save.dat" "$scratch/game/selection/unpacked/world/save.dat"
test -d "$scratch/game/selection/unpacked/world/empty"
test ! -e "$scratch/game/selection/unpacked/ignored.txt"
test "$(sha256sum < "$selected")" = "$checksum_before"
expect_status 409 -H 'Content-Type: application/json' --data-binary "$extract_body" "$list/extract"
expect_status 404 -H 'Content-Type: application/json' --data-binary '{"source":"selection/selected.tar.gz","destination":"shortcut/escape"}' "$list/extract"

python3 - "$scratch/game/selection" <<'PY'
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile
folder = Path(sys.argv[1])
with ZipFile(folder / 'standard.zip', 'w', ZIP_DEFLATED) as archive:
    archive.writestr('maps/level.cfg', 'zip contents')
(folder / 'broken.zip').write_bytes(b'this file is not a ZIP')
PY
expect_status 201 -H 'Content-Type: application/json' --data-binary '{"source":"selection/standard.zip","destination":"selection/from-zip"}' "$list/extract"
test "$(cat "$scratch/game/selection/from-zip/maps/level.cfg")" = 'zip contents'
expect_status 422 -H 'Content-Type: application/json' --data-binary '{"source":"selection/broken.zip","destination":"selection/broken-output"}' "$list/extract"
test ! -e "$scratch/game/selection/broken-output"
if find "$scratch/game/selection" -maxdepth 1 -name '.arcadenode-unpack-*' -print -quit | grep -q .; then
    echo 'Extraction left a temporary directory.' >&2
    exit 1
fi

dd if=/dev/zero of="$scratch/upload.bin" bs=65536 count=17 status=none
printf 'completed world upload' >> "$scratch/upload.bin"
expect_status 201 -H 'Content-Type: application/octet-stream' --data-binary "@$scratch/upload.bin" "$list/upload?path=selection/upload.bin"
cmp "$scratch/upload.bin" "$scratch/game/selection/upload.bin"
expect_status 409 -H 'Content-Type: application/octet-stream' --data-binary "@$scratch/upload.bin" "$list/upload?path=selection/upload.bin"
cmp "$scratch/upload.bin" "$scratch/game/selection/upload.bin"

dd if=/dev/zero of="$scratch/game/selection/large-world.bin" bs=1048576 count=9 status=none
printf 'completed world download' >> "$scratch/game/selection/large-world.bin"
length="$(wc -c < "$scratch/game/selection/large-world.bin")"
expect_status 200 --head "$list/stream?path=selection/large-world.bin"
tr -d '\r' < "$scratch/body" | grep -ix "Content-Length: $length" >/dev/null
expect_status 413 "$list/download?path=selection/large-world.bin"
expect_status 200 "$list/stream?path=selection/large-world.bin"
cmp "$scratch/body" "$scratch/game/selection/large-world.bin"
expect_status 404 "$list/stream?path=outside-link"
truncate -s 536870913 "$scratch/game/selection/oversized-world.bin"
expect_status 413 --head "$list/stream?path=selection/oversized-world.bin"
truncate -s 536870912 "$scratch/game/selection/oversized-world.bin"
expect_status 200 --head "$list/stream?path=selection/oversized-world.bin"
tr -d '\r' < "$scratch/body" | grep -ix 'Content-Length: 536870912' >/dev/null

mkdir -p "$scratch/game/delete-tree/subdir"
printf 'delete only this item' > "$scratch/game/old.log"
printf 'synthetic world' > "$scratch/game/delete-tree/subdir/save.dat"
ln -s /game/server.properties "$scratch/game/delete-tree/subdir/linked-config"
mkfifo "$scratch/game/delete-tree/unsupported-fifo"
expect_status 422 -H 'Content-Type: application/json' --data-binary '{"entries":["old.log","delete-tree"]}' "$list/delete"
test -f "$scratch/game/old.log"
test -f "$scratch/game/delete-tree/subdir/save.dat"
rm -- "$scratch/game/delete-tree/unsupported-fifo"
expect_status 404 -H 'Content-Type: application/json' --data-binary '{"entries":["old.log","missing"]}' "$list/delete"
test -f "$scratch/game/old.log"
expect_status 204 -H 'Content-Type: application/json' --data-binary '{"entries":["old.log","delete-tree"]}' "$list/delete"
test ! -e "$scratch/game/old.log"
test ! -e "$scratch/game/delete-tree"
grep -qx 'motd=ArcadeNode' "$scratch/game/server.properties"

mkdir "$scratch/pterodactyl"
printf 'legacy world' > "$scratch/pterodactyl/save.dat"
chmod 640 "$scratch/pterodactyl/save.dat"
ln -s save.dat "$scratch/pterodactyl/current-save"
legacy_container="$(docker create --name "$server_id" --network none --cap-drop ALL --security-opt no-new-privileges \
    --label Service=Pterodactyl --label ContainerType=server_process --entrypoint /bin/sh \
    --mount "type=bind,src=$scratch/pterodactyl,dst=/home/container" "$image" -c 'exec sleep 90')"
preflight=(dotnet run --project scripts/pterodactyl-migration/Preflight.csproj --no-build --)
docker start "$legacy_container" >/dev/null
if "${preflight[@]}" capture-pterodactyl-volume "$server_id" > "$scratch/running-source.json"; then
    echo 'Running Pterodactyl source was accepted.' >&2
    exit 1
fi
docker stop -t 2 "$legacy_container" >/dev/null
"${preflight[@]}" capture-pterodactyl-volume "$server_id" > "$scratch/source.json"
jq -e --arg id "$legacy_container" '.schema_version == 3 and .links == 1 and .source_process.container_id == $id and .source_process.stopped_at != null and .files == 1' "$scratch/source.json" >/dev/null
"${preflight[@]}" verify-volume "$server_id" "$scratch/source.json" "$scratch/pterodactyl" > "$scratch/verified.json"
jq -e '.matches and .source.tree_sha256 == .destination.tree_sha256' "$scratch/verified.json" >/dev/null
"${preflight[@]}" confirm-pterodactyl-source "$server_id" "$scratch/source.json" --writes-fenced > "$scratch/fence.json"
jq -e --arg id "$legacy_container" '.schema_version == 2 and .source_process.container_id == $id and .writes_fenced and .observed_at != null' "$scratch/fence.json" >/dev/null
cp -a "$scratch/pterodactyl" "$scratch/destination"
chmod 740 "$scratch/destination/save.dat"
if "${preflight[@]}" verify-volume "$server_id" "$scratch/source.json" "$scratch/destination" > "$scratch/changed-mode.json"; then
    echo 'A destination with different file permissions was accepted.' >&2
    exit 1
else
    test "$?" -eq 2
fi
jq -e '.matches == false and .source.bytes == .destination.bytes' "$scratch/changed-mode.json" >/dev/null
chmod 640 "$scratch/destination/save.dat"
"${preflight[@]}" verify-volume "$server_id" "$scratch/source.json" "$scratch/destination" > "$scratch/final-volume.json"
printf 'edited world' > "$scratch/pterodactyl/save.dat"
if "${preflight[@]}" confirm-pterodactyl-source "$server_id" "$scratch/source.json" --writes-fenced > "$scratch/changed-world-fence.json"; then
    echo 'A write against the stopped source did not invalidate the snapshot.' >&2
    exit 1
fi
printf 'legacy world' > "$scratch/pterodactyl/save.dat"
docker start "$legacy_container" >/dev/null
docker stop -t 2 "$legacy_container" >/dev/null
if "${preflight[@]}" confirm-pterodactyl-source "$server_id" "$scratch/source.json" --writes-fenced > "$scratch/stale-fence.json"; then
    echo 'Source restart did not invalidate the copy receipt.' >&2
    exit 1
fi
source scripts/security-probes/directory-pagination-smoke.sh
verify_directory_pages "$list" "$scratch/game" "$scratch" "${auth[@]}"
echo 'Linux node Docker smoke passed.'
