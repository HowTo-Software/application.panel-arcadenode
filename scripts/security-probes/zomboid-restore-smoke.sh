#!/bin/bash
set -euo pipefail

temp_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
scratch="$(mktemp -d "$temp_root/arcadenode-zomboid-restore.XXXXXX")"
[[ "$scratch" == "$temp_root"/arcadenode-zomboid-restore.* ]]
trap 'rm -rf -- "$scratch"' EXIT

sed -e 's|^PZ_HOME="/home/pzserver"$|PZ_HOME="$TEST_PZ_HOME"|' \
    -e 's|^STEAMCMD="/usr/bin/steamcmd"$|STEAMCMD="$TEST_STEAMCMD"|' \
    game-servers/project-zomboid/entrypoint.sh > "$scratch/entrypoint.sh"
cat > "$scratch/launcher" <<'LAUNCHER'
#!/bin/bash
printf '%s\n' "$@" > "$HOME/arguments"
printf '%s\n' "$PWD" > "$HOME/installation"
printf '%s\n' "$PATH" > "$HOME/launcher-path"
LAUNCHER

seed_restored_state() {
    local state="$1"
    mkdir -p "$state/Server" "$state/Saves/Multiplayer/Fort" "$state/db"
    printf 'DefaultPort=16261\nUDPPort=16262\nRCONPassword=\n' > "$state/Server/Fort.ini"
    printf 'original save bytes' > "$state/Saves/Multiplayer/Fort/map.bin"
    printf 'original database bytes' > "$state/db/Fort.db"
}

seed_launcher_config() {
    printf '%s\n' '{"mainClass":"zombie/network/GameServer","classpath":["projectzomboid.jar"],"vmArgs":["-Xmx8g","-Xms6g","-XX:SoftMaxHeapSize=7g","-Djava.awt.headless=true"]}' > "$1"
    chmod 640 "$1"
}

export ARCADENODE_RESTORED_VOLUME=true ARCADENODE_JAVA_HEAP_MIB=3072
export SERVER_NAME=Fort SERVER_PORT=16261 STEAM_PORT=16262 RCON_PORT=27015
export ARCADENODE_PUBLISHED_PORTS=16261,16262

for layout in pterodactyl managed; do
    export TEST_PZ_HOME="$scratch/$layout"
    if [[ "$layout" == pterodactyl ]]; then
        installation="$TEST_PZ_HOME"
        state="$TEST_PZ_HOME/.cache"
        launcher=ProjectZomboid64
        export SERVER_NAME='Fort West'
        mkdir -p "$installation/jre64/bin"
        printf '#!/bin/bash\nexit 0\n' > "$installation/jre64/bin/java"
        chmod 700 "$installation/jre64/bin/java"
    else
        installation="$TEST_PZ_HOME/server"
        state="$TEST_PZ_HOME/Zomboid"
        launcher=start-server.sh
        export SERVER_NAME=Fort
        mkdir -p "$installation"
    fi
    cp "$scratch/launcher" "$installation/$launcher"
    chmod 700 "$installation/$launcher"
    seed_restored_state "$state"
    if [[ "$layout" == pterodactyl ]]; then
        mv "$state/Server/Fort.ini" "$state/Server/$SERVER_NAME.ini"
        mv "$state/Saves/Multiplayer/Fort" "$state/Saves/Multiplayer/$SERVER_NAME"
        mv "$state/db/Fort.db" "$state/db/$SERVER_NAME.db"
    elif [[ "$(uname -s)" == Linux ]]; then
        mkdir "$TEST_PZ_HOME/data"
        for component in Server Saves db; do
            mv "$state/$component" "$TEST_PZ_HOME/data/$component"
            ln -s "../data/$component" "$state/$component"
        done
    fi
    game_config="$state/Server/$SERVER_NAME.ini"
    config="$installation/ProjectZomboid64.json"
    seed_launcher_config "$config"
    mode="$(stat -c %a "$config")"

    bash "$scratch/entrypoint.sh"
    test "$(cat "$TEST_PZ_HOME/installation")" = "$installation"
    printf '%s\n' -servername "$SERVER_NAME" -ip 0.0.0.0 -port 16261 -udpport 16262 "-cachedir=$state" > "$scratch/expected-arguments"
    cmp "$scratch/expected-arguments" "$TEST_PZ_HOME/arguments"
    NO_STEAM=true bash "$scratch/entrypoint.sh"
    cp "$scratch/expected-arguments" "$scratch/non-steam-arguments"
    printf '%s\n' -nosteam >> "$scratch/non-steam-arguments"
    cmp "$scratch/non-steam-arguments" "$TEST_PZ_HOME/arguments"
    if NO_STEAM='true -arbitrary-flag' bash "$scratch/entrypoint.sh" > "$scratch/rejected" 2>&1; then
        echo 'The restored Zomboid entrypoint accepted extra Steam mode arguments.' >&2
        exit 1
    fi
    if [[ "$layout" == pterodactyl ]]; then
        [[ "$(cat "$TEST_PZ_HOME/launcher-path")" == "$installation/jre64/bin:"* ]]
    fi
    test "$(cat "$state/Saves/Multiplayer/$SERVER_NAME/map.bin")" = 'original save bytes'
    test "$(cat "$state/db/$SERVER_NAME.db")" = 'original database bytes'
    test "$(stat -c %a "$config")" = "$mode"
    jq -e '.mainClass == "zombie/network/GameServer" and .classpath == ["projectzomboid.jar"] and
        .vmArgs == ["-Djava.awt.headless=true", "-Xmx3072m"]' "$config" >/dev/null

    cp "$config" "$scratch/previous-config"
    bash "$scratch/entrypoint.sh"
    cmp "$scratch/previous-config" "$config"

    if [[ "$layout" == managed && -L "$state/Server" ]]; then
        ln -sfn "$TEST_PZ_HOME/data/Server" "$state/Server"
        if bash "$scratch/entrypoint.sh" > "$scratch/rejected" 2>&1; then
            echo 'The restored Zomboid entrypoint accepted an absolute state link.' >&2
            exit 1
        fi
        grep -Fq 'Invalid restored Zomboid state link' "$scratch/rejected"
        ln -sfn '../data/Server' "$state/Server"
    fi

    if ARCADENODE_PUBLISHED_PORTS=16261 bash "$scratch/entrypoint.sh" > "$scratch/rejected" 2>&1; then
        echo 'The entrypoint accepted an unpublished direct connection port.' >&2
        exit 1
    fi
    grep -Fq 'published direct connection port' "$scratch/rejected"
    printf 'DefaultPort=16261\nUDPPort=16262\nRCONPort=27015\nRCONPassword=synthetic-fixture-only\n' > "$game_config"
    RCON_PORT=27016 bash "$scratch/entrypoint.sh"
    if RCON_PORT=27016 ARCADENODE_PUBLISHED_PORTS=16261,16262,27016 bash "$scratch/entrypoint.sh" > "$scratch/rejected" 2>&1; then
        echo 'The entrypoint accepted a conflicting private RCON port.' >&2
        exit 1
    fi
    grep -Fq 'ports or RCON credentials need review' "$scratch/rejected"
    if grep -Fq 'synthetic-fixture-only' "$scratch/rejected"; then exit 1; fi
    printf '%s\n' '{"vmArgs":[true]}' > "$config"
    cp "$config" "$scratch/invalid-config"
    if bash "$scratch/entrypoint.sh" > "$scratch/rejected" 2>&1; then
        echo 'The entrypoint accepted an invalid launcher configuration.' >&2
        exit 1
    fi
    cmp "$scratch/invalid-config" "$config"
    test -z "$(find "$installation" -maxdepth 1 -name '.arcadenode-jvm.*' -print -quit)"
done

echo 'Project Zomboid restore preserved both volume layouts and applied the managed launcher heap.'

export TEST_STEAMCMD="$scratch/steamcmd" TEST_LAUNCHER="$scratch/launcher"
cat > "$TEST_STEAMCMD" <<'STEAMCMD'
#!/bin/bash
set -euo pipefail
printf '%s\n' "$@" > "$TEST_PZ_HOME/steam-arguments"
printf 'invoked\n' >> "$TEST_PZ_HOME/steam-calls"
test "$5" = +force_install_dir
cp "$TEST_LAUNCHER" "$6/start-server.sh"
chmod 700 "$6/start-server.sh"
printf '%s\n' '{"vmArgs":["-Xmx8g"]}' > "$6/ProjectZomboid64.json"
if [[ "${TEST_STEAM_FAILURE:-0}" == 1 ]]; then exit 17; fi
STEAMCMD
chmod 700 "$TEST_STEAMCMD"

export ARCADENODE_RESTORED_VOLUME=false ADMIN_PASSWORD='synthetic-zomboid-fixture-only'
export AUTO_UPDATE=0 BETA_BRANCH='' STEAM_PORT=27062 SERVER_PORT=27061
export TEST_PZ_HOME="$scratch/existing"
installation="$TEST_PZ_HOME/server"
state="$TEST_PZ_HOME/Zomboid"
mkdir -p "$installation"
cp "$TEST_LAUNCHER" "$installation/start-server.sh"
chmod 700 "$installation/start-server.sh"
seed_launcher_config "$installation/ProjectZomboid64.json"
seed_restored_state "$state"
chmod 600 "$state/db/Fort.db"
db_mode="$(stat -c %a "$state/db/Fort.db")"
cp "$state/Server/Fort.ini" "$scratch/original.ini"
bash "$scratch/entrypoint.sh"
test ! -e "$TEST_PZ_HOME/steam-calls"
test "$(cat "$state/Saves/Multiplayer/Fort/map.bin")" = 'original save bytes'
test "$(cat "$state/db/Fort.db")" = 'original database bytes'
test "$(stat -c %a "$state/db/Fort.db")" = "$db_mode"
cmp "$scratch/original.ini" "$state/Server/Fort.ini"
grep -Fxq -- "-cachedir=$state" "$TEST_PZ_HOME/arguments"
grep -Fxq '27062' "$TEST_PZ_HOME/arguments"

mkdir -p "$TEST_PZ_HOME/data/Saves/Multiplayer/Fort"
printf 'separate save' > "$TEST_PZ_HOME/data/Saves/Multiplayer/Fort/map.bin"
if AUTO_UPDATE=1 bash "$scratch/entrypoint.sh" > "$scratch/split-state" 2>&1; then
    echo 'Startup accepted overlapping save layouts.' >&2
    exit 1
fi
grep -Fq 'Existing Saves state in both layouts' "$scratch/split-state"
test "$(cat "$state/Saves/Multiplayer/Fort/map.bin")" = 'original save bytes'
test "$(cat "$TEST_PZ_HOME/data/Saves/Multiplayer/Fort/map.bin")" = 'separate save'
test ! -e "$TEST_PZ_HOME/steam-calls"

if [[ "$(uname -s)" == Linux ]]; then
    export TEST_PZ_HOME="$scratch/linked-install"
    mkdir -p "$TEST_PZ_HOME/server" "$TEST_PZ_HOME/Zomboid"
    cp "$TEST_LAUNCHER" "$TEST_PZ_HOME/server/start-server.sh"
    chmod 700 "$TEST_PZ_HOME/server/start-server.sh"
    seed_launcher_config "$TEST_PZ_HOME/server/ProjectZomboid64.json"
    seed_restored_state "$TEST_PZ_HOME/data"
    ln -s "$TEST_PZ_HOME/data/Server" "$TEST_PZ_HOME/Zomboid/Server"
    ln -s "$TEST_PZ_HOME/data/db" "$TEST_PZ_HOME/Zomboid/db"
    bash "$scratch/entrypoint.sh"
    test "$(readlink "$TEST_PZ_HOME/Zomboid/Server")" = "$TEST_PZ_HOME/data/Server"
    test "$(readlink "$TEST_PZ_HOME/Zomboid/db")" = "$TEST_PZ_HOME/data/db"
    test "$(readlink "$TEST_PZ_HOME/Zomboid/Saves")" = '../data/Saves'
    test "$(cat "$TEST_PZ_HOME/Zomboid/Saves/Multiplayer/Fort/map.bin")" = 'original save bytes'
    test "$(cat "$TEST_PZ_HOME/Zomboid/db/Fort.db")" = 'original database bytes'
fi

export TEST_PZ_HOME="$scratch/new-install" BETA_BRANCH=legacy41
if TEST_STEAM_FAILURE=1 bash "$scratch/entrypoint.sh" > "$scratch/install-failed" 2>&1; then
    echo 'An interrupted Steam installation reached the launcher.' >&2
    exit 1
fi
test "$(wc -l < "$TEST_PZ_HOME/steam-calls")" -eq 1
test -f "$TEST_PZ_HOME/.cache/steam-install.pending"
test ! -e "$TEST_PZ_HOME/arguments"

bash "$scratch/entrypoint.sh"
test ! -e "$TEST_PZ_HOME/.cache/steam-install.pending"
test "$(wc -l < "$TEST_PZ_HOME/steam-calls")" -eq 2
printf '%s\n' '+@ShutdownOnFailedCommand' 1 '+@sSteamCmdForcePlatformType' linux +force_install_dir "$TEST_PZ_HOME/server" \
    +login anonymous +app_info_update 1 +app_update 380870 -beta legacy41 validate +quit > "$scratch/expected-steam-arguments"
cmp "$scratch/expected-steam-arguments" "$TEST_PZ_HOME/steam-arguments"
for component in Saves Server Logs db; do
    test -d "$TEST_PZ_HOME/Zomboid/$component"
    test ! -L "$TEST_PZ_HOME/Zomboid/$component"
done

bash "$scratch/entrypoint.sh"
test "$(wc -l < "$TEST_PZ_HOME/steam-calls")" -eq 2
AUTO_UPDATE=1 bash "$scratch/entrypoint.sh"
test "$(wc -l < "$TEST_PZ_HOME/steam-calls")" -eq 3
cmp "$scratch/expected-steam-arguments" "$TEST_PZ_HOME/steam-arguments"
echo 'Project Zomboid startup retained existing saves and retried only the requested Steam branch.'
