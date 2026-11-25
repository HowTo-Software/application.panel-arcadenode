#!/bin/bash
set -euo pipefail

image="${1:-arcadenode-bedrock-check}"
source "$(dirname "${BASH_SOURCE[0]}")/official-game-cutover-common.sh"
require_local_game_runner "$image"

scratch="$(mktemp -d /tmp/arcadenode-bedrock-cutover.XXXXXXXX)"
[[ "$scratch" == /tmp/arcadenode-bedrock-cutover.* ]]
installer=''
source_container=''
destination_container=''
cleanup() {
    local status=$?
    trap - EXIT
    for container in "$source_container" "$destination_container" "$installer"; do
        if [[ -z "$container" ]]; then continue; fi
        if [[ "$status" != 0 ]]; then docker logs --tail 35 "$container" >&2 || true; fi
        docker rm -f "$container" >/dev/null 2>&1 || true
    done
    sudo -n rm -rf -- "$scratch"
    exit "$status"
}
trap cleanup EXIT
chmod 0711 "$scratch"
server_id="$(cat /proc/sys/kernel/random/uuid)"

publish_cutover_preflight "$scratch/preflight"
preflight="$scratch/preflight/Preflight"
sudo -n install -d -m 0700 -o 10001 -g 10001 "$scratch/source" "$scratch/destination"
isolated=(--user 10001:10001 --read-only --cap-drop ALL --security-opt no-new-privileges
    --pids-limit 512 --memory 2g --memory-swap 2g --cpus 2
    --tmpfs /tmp:rw,nosuid,nodev,noexec,size=64m,mode=1777)

# The installation container alone needs network access for the official package.
installer="bedrock-install-$server_id"
docker run --rm --name "$installer" "${isolated[@]}" \
    --entrypoint /usr/local/bin/arcadenode-download-bedrock \
    --mount "type=bind,src=$scratch/source,dst=/home/container" "$image" LATEST /home/container
installer=''
sudo -n install -d -m 0700 -o 10001 -g 10001 "$scratch/source/worlds"
sudo -n setpriv --reuid=10001 --regid=10001 --clear-groups tee "$scratch/source/server.properties" >/dev/null <<'PROPERTIES'
server-name=ArcadeNode migration acceptance
level-name=ArcadeCutover
level-seed=902310992
gamemode=creative
online-mode=false
allow-cheats=true
enable-lan-visibility=false
server-port=19132
server-portv6=19133
view-distance=5
tick-distance=4
max-threads=2
PROPERTIES

stop_game() {
    send_console "$1" stop
    wait_for_clean_exit "$1"
}

source_container="$(docker run -dit --name "$server_id" --network none "${isolated[@]}" \
    --label Service=Pterodactyl --label ContainerType=server_process \
    --mount "type=bind,src=$scratch/source,dst=/home/container" \
    --workdir /home/container --env LD_LIBRARY_PATH=/home/container \
    --entrypoint /home/container/bedrock_server "$image")"
wait_for_log "$source_container" 'Server started\.' 180

if sudo -n "$preflight" capture-pterodactyl-volume "$server_id" > "$scratch/live.json"; then
    echo 'The final volume capture accepted a running source.' >&2
    exit 1
fi

copy_status=0
sudo -n rsync -rltpx --safe-links --chown=10001:10001 "$scratch/source/" "$scratch/destination/" || copy_status=$?
[[ "$copy_status" == 0 || "$copy_status" == 24 ]]
send_console "$source_container" 'scoreboard objectives add ArcadeProof dummy' \
    'scoreboard players set MigrationProbe ArcadeProof 42' 'scoreboard players list MigrationProbe'
wait_for_log "$source_container" '(ArcadeProof.*42|42.*ArcadeProof)' 30
stop_game "$source_container"
sudo -n test -s "$scratch/source/worlds/ArcadeCutover/level.dat"
sudo -n test -d "$scratch/source/worlds/ArcadeCutover/db"

sudo -n "$preflight" capture-pterodactyl-volume "$server_id" > "$scratch/source.json"
jq -e --arg id "$source_container" '.schema_version == 3 and .source_process.container_id == $id and .source_process.stopped_at != null' \
    "$scratch/source.json" >/dev/null
sudo -n rsync -rltpx --safe-links --delete-delay --checksum --chown=10001:10001 \
    "$scratch/source/" "$scratch/destination/"
verify_destination() {
    local report="$1"
    sudo -n setpriv --reuid=10001 --regid=10001 --clear-groups --no-new-privs "$preflight" \
        verify-volume "$server_id" - "$scratch/destination" --minecraft-bedrock < "$scratch/source.json" > "$report"
    jq -e '.matches and .source.tree_sha256 == .destination.tree_sha256 and
        .minecraft_bedrock == {layout:"pterodactyl",game_port:19132,ipv6_port:19133}' "$report" >/dev/null
}
verify_destination "$scratch/verified.json"
destination_container="$(docker create -it --name "bedrock-restored-$server_id" --network none "${isolated[@]}" \
    --env ARCADENODE_RESTORED_VOLUME=true --env SERVER_PORT=19132 --env SERVER_PORTV6=19133 \
    --mount "type=bind,src=$scratch/destination,dst=/home/bedrock" "$image")"
test "$(docker inspect --format '{{.State.Status}}' "$destination_container")" = created

sudo -n "$preflight" confirm-pterodactyl-source "$server_id" "$scratch/source.json" --writes-fenced > "$scratch/fence.json"
jq -e --arg digest "$(jq -er '.tree_sha256' "$scratch/source.json")" \
    '.schema_version == 2 and .writes_fenced and .tree_sha256 == $digest' "$scratch/fence.json" >/dev/null
verify_destination "$scratch/final-volume.json"

docker start "$destination_container" >/dev/null
wait_for_log "$destination_container" 'Server started\.' 180
send_console "$destination_container" 'scoreboard players list MigrationProbe'
wait_for_log "$destination_container" '(ArcadeProof.*42|42.*ArcadeProof)' 30
stop_game "$destination_container"
sudo -n cmp "$scratch/source/server.properties" "$scratch/destination/server.properties"
sudo -n cmp "$scratch/source/.version" "$scratch/destination/.version"
sudo -n cmp "$scratch/source/worlds/ArcadeCutover/levelname.txt" "$scratch/destination/worlds/ArcadeCutover/levelname.txt"
echo "Official Bedrock $(sudo -n cat "$scratch/destination/.version") preserved the saved scoreboard after cutover."
