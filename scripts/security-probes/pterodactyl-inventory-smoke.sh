#!/bin/bash
set -euo pipefail

[[ "$(uname -s)" == Linux ]]
php -r 'exit(extension_loaded("pdo_mysql") ? 0 : 1);'
unset DOCKER_CONTEXT
export DOCKER_HOST=unix:///var/run/docker.sock

work="$(mktemp -d /tmp/arcadenode-ptero-inventory.XXXXXXXX)"
[[ "$work" == /tmp/arcadenode-ptero-inventory.* ]]
container=''
cleanup() {
    local status=$?
    trap - EXIT
    if [[ -n "$container" ]]; then
        if [[ "$status" != 0 ]]; then docker logs --tail 25 "$container" >&2 || true; fi
        docker rm -fv "$container" >/dev/null
    fi
    rm -rf -- "$work"
    exit "$status"
}
trap cleanup EXIT
umask 077
# The database and credentials exist only for this isolated test.
export MARIADB_ROOT_PASSWORD="InventorySmoke!$(openssl rand -hex 16)"
inventory_password="InventoryRead!$(openssl rand -hex 16)"
test_hash="$(php -r 'echo password_hash("Old7!", PASSWORD_BCRYPT, ["cost" => 10]);')"
printf '%s\n' "$inventory_password" > "$work/password"
mkdir -m 0777 "$work/socket"
container="$(docker run -d --network none -e MARIADB_ROOT_PASSWORD \
    --mount "type=bind,src=$work/socket,dst=/run/mysqld" mariadb:11.8)"

admin() {
    docker exec -i "$container" sh -c 'MYSQL_PWD="$MARIADB_ROOT_PASSWORD" exec mariadb --protocol=socket --user=root "$@"' _ "$@"
}
for attempt in {1..45}; do
    if admin --batch --skip-column-names -e 'SELECT 1' </dev/null >/dev/null 2>&1 &&
        [[ "$(docker exec "$container" cat /proc/1/comm)" == mariadbd ]]; then break; fi
    if [[ "$attempt" == 45 ]]; then exit 1; fi
    sleep 2
done
admin <<SQL
CREATE DATABASE restored_ptero;
USE restored_ptero;
CREATE TABLE users (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    uuid CHAR(36) NOT NULL UNIQUE,
    username VARCHAR(191) NOT NULL,
    email VARCHAR(191) NOT NULL,
    name_first VARCHAR(191) NULL,
    name_last VARCHAR(191) NULL,
    password VARCHAR(191) NOT NULL,
    use_totp TINYINT UNSIGNED NOT NULL,
    root_admin TINYINT UNSIGNED NOT NULL
) ENGINE=InnoDB;
INSERT INTO users (uuid, username, email, name_first, name_last, password, use_totp, root_admin) VALUES
    ('11111111-1111-4111-8111-111111111111', 'legacy-owner', 'legacy-owner@example.test', NULL, 'Owner', '$test_hash', 0, 0),
    ('22222222-2222-4222-8222-222222222222', 'legacy-mfa', 'legacy-mfa@example.test', NULL, NULL, '$test_hash', 1, 0),
    ('33333333-3333-4333-8333-333333333333', 'legacy-admin', 'legacy-admin@example.test', NULL, NULL, '$test_hash', 0, 1);
CREATE USER 'inventory'@'localhost' IDENTIFIED BY '$inventory_password';
GRANT SELECT ON restored_ptero.* TO 'inventory'@'localhost';
SQL

export PTERO_INVENTORY_DSN="mysql:unix_socket=$work/socket/mysqld.sock;dbname=restored_ptero;charset=utf8mb4"
export PTERO_INVENTORY_USER=inventory PTERO_INVENTORY_PASSWORD_FILE="$work/password"
php scripts/pterodactyl-migration/inventory.php --accounts > "$work/accounts.json"
jq -e --arg hash "$test_hash" '.schema_version == 1 and .users == [{
    uuid:"11111111-1111-4111-8111-111111111111",username:"legacy-owner",email:"legacy-owner@example.test",
    name_first:null,name_last:"Owner",password:$hash,use_totp:false,root_admin:false
}]' "$work/accounts.json" >/dev/null
if PTERO_INVENTORY_DSN='mysql:host=127.0.0.1;dbname=restored_ptero' PTERO_INVENTORY_SSL_CA='' \
    php scripts/pterodactyl-migration/inventory.php --accounts > "$work/refused" 2>&1; then
    echo 'The account exporter accepted TCP without a trusted CA.' >&2
    exit 1
fi
grep -Fq 'Set a trusted CA file' "$work/refused"
echo 'MariaDB account export retained the original bcrypt hash and excluded administrators and TOTP accounts from the ordinary export.'
