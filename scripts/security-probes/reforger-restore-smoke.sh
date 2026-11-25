#!/bin/bash
set -euo pipefail

temp_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
scratch="$(mktemp -d "$temp_root/arcadenode-reforger-restore.XXXXXX")"
[[ "$scratch" == "$temp_root"/arcadenode-reforger-restore.* ]]
trap 'rm -rf -- "$scratch"' EXIT

sed 's|^REFORGER_HOME="/home/reforger"$|REFORGER_HOME="$TEST_REFORGER_HOME"|' \
    game-servers/arma-reforger/entrypoint.sh > "$scratch/entrypoint.sh"
cat > "$scratch/launcher" <<'LAUNCHER'
#!/bin/bash
printf '%s\n' "$@" > "$TEST_REFORGER_HOME/arguments"
printf '%s\n' "$PWD" > "$TEST_REFORGER_HOME/installation"
LAUNCHER

export ARCADENODE_RESTORED_VOLUME=true SERVER_PORT=2001 ARCADENODE_PUBLISHED_PORTS=2001,17777
for layout in pterodactyl managed; do
    export TEST_REFORGER_HOME="$scratch/$layout"
    if [[ "$layout" == pterodactyl ]]; then
        installation="$TEST_REFORGER_HOME"
        profile="$installation/profile"
        addons="$installation/addons"
        config="$installation/config.json"
        mkdir -p "$addons/data" "$profile"
        touch "$addons/data/ArmaReforger.gproj"
        launch_options=(-gproj "$addons/data/ArmaReforger.gproj" -backendlog -nothrow -listScenarios
            -addonTempDir "$installation/tmp")
    else
        installation="$TEST_REFORGER_HOME/server"
        profile="$TEST_REFORGER_HOME/data/profile"
        addons="$TEST_REFORGER_HOME/data/addons"
        config="$TEST_REFORGER_HOME/data/configs/server.json"
        mkdir -p "$installation" "$addons" "$profile" "${config%/*}"
        launch_options=(-addonsDir "$addons" -logLevel normal -logStats 60000)
    fi
    cp "$scratch/launcher" "$installation/ArmaReforgerServer"
    chmod 700 "$installation/ArmaReforgerServer"
    printf '%s\n' '{"bindPort":2001,"publicPort":2001,"a2s":{"port":17777},"game":{"name":"Restored server","passwordAdmin":"synthetic-fixture-only"}}' > "$config"
    printf '%s\n' 'existing persisted profile' > "$profile/world.fixture"
    cp "$config" "$scratch/expected-config"
    printf '%s\n' -config "$config" -profile "$profile" "${launch_options[@]}" > "$scratch/expected-arguments"

    if ARCADENODE_PUBLISHED_PORTS=2001 bash "$scratch/entrypoint.sh" > "$scratch/rejected" 2>&1; then
        echo 'Reforger accepted an unreserved A2S listener.' >&2
        exit 1
    fi
    grep -Fq 'listener or credential configuration needs review' "$scratch/rejected"
    test ! -e "$TEST_REFORGER_HOME/arguments"
    test ! -e "$TEST_REFORGER_HOME/tmp"

    bash "$scratch/entrypoint.sh"
    test "$(cat "$TEST_REFORGER_HOME/installation")" = "$installation"
    cmp "$scratch/expected-arguments" "$TEST_REFORGER_HOME/arguments"
    cmp "$scratch/expected-config" "$config"
    test "$(cat "$profile/world.fixture")" = 'existing persisted profile'
done
