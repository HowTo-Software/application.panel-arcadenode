#!/bin/bash
set -euo pipefail

image="${1:-arcadenode-minecraft-check}"
source "$(dirname "${BASH_SOURCE[0]}")/official-game-cutover-common.sh"
require_local_game_runner "$image"

scratch="$(mktemp -d /tmp/arcadenode-java-cutover.XXXXXXXX)"
[[ "$scratch" == /tmp/arcadenode-java-cutover.* ]]
source_container=''
destination_container=''
installer=''
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

installer="vanilla-install-$server_id"
docker run --rm "${isolated[@]}" --name "$installer" \
    --entrypoint /usr/local/bin/arcadenode-download-vanilla \
    --mount "type=bind,src=$scratch/source,dst=/home/container" "$image" latest /home/container/server.jar
installer=''
runtime="$(sudo -n cat "$scratch/source/server.jar.java-runtime")"
case "$runtime" in
    21) java_bin=/opt/java/openjdk/bin/java ;;
    25) java_bin=/opt/java/java25/bin/java ;;
    *) echo 'Unexpected Java runtime from the Mojang metadata.' >&2; exit 1 ;;
esac
sudo -n setpriv --reuid=10001 --regid=10001 --clear-groups tee "$scratch/source/eula.txt" >/dev/null <<'EULA'
eula=true
EULA
sudo -n setpriv --reuid=10001 --regid=10001 --clear-groups tee "$scratch/source/server.properties" >/dev/null <<'PROPERTIES'
level-name=ArcadeCutover
level-seed=902310992
level-type=minecraft:flat
server-port=25565
online-mode=false
enable-query=false
enable-rcon=false
view-distance=5
simulation-distance=4
PROPERTIES

source_container="$(docker run -dit --name "$server_id" --network none "${isolated[@]}" \
    --label Service=Pterodactyl --label ContainerType=server_process \
    --mount "type=bind,src=$scratch/source,dst=/home/container" \
    --workdir /home/container --entrypoint "$java_bin" "$image" -Xmx1200M -jar server.jar --nogui)"
wait_for_log "$source_container" 'Done \([0-9.]+s\)!' 180
if sudo -n "$preflight" capture-pterodactyl-volume "$server_id" > "$scratch/live.json"; then
    echo 'The final volume capture accepted a running source.' >&2
    exit 1
fi
copy_status=0
sudo -n rsync -rltpx --safe-links --chown=10001:10001 "$scratch/source/" "$scratch/destination/" || copy_status=$?
[[ "$copy_status" == 0 || "$copy_status" == 24 ]]

send_console "$source_container" 'scoreboard objectives add ArcadeProof dummy' \
    'scoreboard players set MigrationProbe ArcadeProof 42' 'scoreboard players get MigrationProbe ArcadeProof'
wait_for_log "$source_container" 'MigrationProbe has 42' 30
send_console "$source_container" stop
wait_for_clean_exit "$source_container"
sudo -n test -s "$scratch/source/ArcadeCutover/level.dat"

sudo -n "$preflight" capture-pterodactyl-volume "$server_id" > "$scratch/source.json"
jq -e --arg id "$source_container" '.schema_version == 3 and .source_process.container_id == $id and .source_process.stopped_at != null' \
    "$scratch/source.json" >/dev/null
sudo -n rsync -rltpx --safe-links --delete-delay --checksum --chown=10001:10001 "$scratch/source/" "$scratch/destination/"
verify_destination() {
    sudo -n setpriv --reuid=10001 --regid=10001 --clear-groups --no-new-privs "$preflight" \
        verify-volume "$server_id" - "$scratch/destination" < "$scratch/source.json" > "$1"
    jq -e '.matches and .source.tree_sha256 == .destination.tree_sha256' "$1" >/dev/null
}
verify_destination "$scratch/verified.json"
destination_container="$(docker create -it --name "vanilla-restored-$server_id" --network none "${isolated[@]}" \
    --env ARCADENODE_RESTORED_VOLUME=true --env ARCADENODE_JAVA_HEAP_MIB=1200 --env "JAVA_RUNTIME=$runtime" \
    --env SERVER_PORT=25565 --env SERVER_TYPE=vanilla \
    --mount "type=bind,src=$scratch/destination,dst=/home/minecraft" "$image")"
test "$(docker inspect --format '{{.State.Status}}' "$destination_container")" = created
sudo -n "$preflight" confirm-pterodactyl-source "$server_id" "$scratch/source.json" --writes-fenced > "$scratch/fence.json"
jq -e --arg digest "$(jq -er '.tree_sha256' "$scratch/source.json")" \
    '.schema_version == 2 and .writes_fenced and .tree_sha256 == $digest' "$scratch/fence.json" >/dev/null
verify_destination "$scratch/final-volume.json"

docker start "$destination_container" >/dev/null
wait_for_log "$destination_container" 'Done \([0-9.]+s\)!' 180
send_console "$destination_container" 'scoreboard players get MigrationProbe ArcadeProof'
wait_for_log "$destination_container" 'MigrationProbe has 42' 30
send_console "$destination_container" stop
wait_for_clean_exit "$destination_container"
sudo -n cmp "$scratch/source/server.jar" "$scratch/destination/server.jar"
diff -u <(sudo -n sed '/^#/d' "$scratch/source/server.properties" | LC_ALL=C sort) \
        <(sudo -n sed '/^#/d' "$scratch/destination/server.properties" | LC_ALL=C sort)
echo "Official Minecraft Java preserved the saved scoreboard after cutover using Java $runtime."
