<?php

declare(strict_types=1);

/** Collect metadata from a restored Pterodactyl database with a SELECT-only account. */
function collectInventory(PDO $source): array
{
    // Use an explicit allowlist. Password hashes, TOTP secrets, daemon tokens,
    // startup commands, environment values and integration keys stay at the source.
    $queries = [
        'users' => 'SELECT id, uuid, root_admin, use_totp FROM users ORDER BY id',
        'nodes' => 'SELECT id, uuid, name, fqdn, scheme, daemonListen AS daemon_port,
                          memory, memory_overallocate, disk, disk_overallocate, maintenance_mode FROM nodes ORDER BY id',
        'eggs' => 'SELECT id, uuid, nest_id, name FROM eggs ORDER BY id',
        'servers' => 'SELECT id, uuid, uuidShort AS identifier, node_id, owner_id, nest_id, egg_id,
                             allocation_id, status AS panel_status, image, memory, swap, disk, io, cpu,
                             threads, oom_disabled FROM servers ORDER BY id',
        'allocations' => 'SELECT id, node_id, server_id, ip, port FROM allocations ORDER BY id',
        'subusers' => 'SELECT user_id, server_id FROM subusers ORDER BY server_id, user_id',
        'schedules' => 'SELECT id, server_id, is_active FROM schedules ORDER BY id',
        'tasks' => 'SELECT id, schedule_id, action FROM tasks ORDER BY id',
        'backups' => 'SELECT uuid, server_id, disk, is_successful, bytes, completed_at FROM backups
                      WHERE deleted_at IS NULL ORDER BY id',
        'databases' => 'SELECT id, server_id, database_host_id FROM `databases` ORDER BY id',
    ];

    $inventory = [
        'schema_version' => 1,
        'captured_at' => gmdate('Y-m-d\TH:i:s\Z'),
        'live_runtime_checked' => false,
    ];
    foreach ($queries as $name => $query) {
        try {
            $inventory[$name] = $source->query($query)->fetchAll(PDO::FETCH_ASSOC);
        } catch (PDOException) {
            throw new RuntimeException("Could not read the expected Pterodactyl schema for {$name}.");
        }
    }

    return $inventory;
}

function decryptTotpSecret(string $ciphertext, Illuminate\Encryption\Encrypter $encrypter): string
{
    try {
        $serialized = $encrypter->decryptString($ciphertext);
    } catch (Throwable) {
        throw new RuntimeException('A TOTP secret could not be decrypted with the source key.');
    }
    if (!preg_match('/^s:(\d+):"([A-Z2-7=]+)";$/iD', $serialized, $matches) ||
        (int) $matches[1] !== strlen($matches[2])) {
        throw new RuntimeException('A TOTP secret has an unsupported format.');
    }
    return $matches[2];
}

function collectSchedules(PDO $source): array
{
    $tasks = [];
    foreach ($source->query(
        'SELECT schedule_id, sequence_id, action, payload, time_offset, continue_on_failure FROM tasks ORDER BY schedule_id, sequence_id, id'
    )->fetchAll(PDO::FETCH_ASSOC) as $task) {
        $scheduleId = $task['schedule_id'];
        unset($task['schedule_id']);
        $task['continue_on_failure'] = (bool) $task['continue_on_failure'];
        $tasks[$scheduleId][] = $task;
    }
    $schedules = [];
    foreach ($source->query(
        'SELECT s.id, s.name, v.uuid AS server_uuid, s.is_active AS was_active, s.only_when_online,
                s.cron_minute, s.cron_hour, s.cron_day_of_month, s.cron_month, s.cron_day_of_week
         FROM schedules s LEFT JOIN servers v ON v.id = s.server_id ORDER BY s.id'
    )->fetchAll(PDO::FETCH_ASSOC) as $row) {
        if ($row['server_uuid'] === null) throw new RuntimeException('A source schedule has no server UUID.');
        $schedules[] = [
            'id' => $row['id'], 'server_uuid' => $row['server_uuid'], 'name' => $row['name'],
            'was_active' => (bool) $row['was_active'], 'only_when_online' => (bool) $row['only_when_online'],
            'cron_expression' => implode(' ', [$row['cron_minute'], $row['cron_hour'], $row['cron_day_of_month'],
                $row['cron_month'], $row['cron_day_of_week']]),
            'tasks' => $tasks[$row['id']] ?? [],
        ];
        unset($tasks[$row['id']]);
    }
    if ($tasks !== []) throw new RuntimeException('A source task has no schedule.');
    return ['schema_version' => 1, 'schedules' => $schedules];
}

function readServerSettingsPlan(string $path): array
{
    if (!is_file($path) || !is_readable($path)) throw new RuntimeException('Set a readable server mapping file.');
    $json = file_get_contents($path, false, null, 0, 262145);
    if ($json === false || strlen($json) > 262144) throw new RuntimeException('Use a server mapping file of at most 256 KiB.');
    $plan = json_decode($json, false, 24, JSON_THROW_ON_ERROR);
    if (!is_object($plan) || ($plan->schema_version ?? null) !== 1 || !is_array($plan->profiles ?? null) ||
        count($plan->profiles) < 1 || count($plan->profiles) > 512)
        throw new RuntimeException('Use a version 1 migration mapping with explicit egg, image and variable selections.');

    $profiles = [];
    foreach ($plan->profiles as $item) {
        if (!is_object($item) || !is_int($item->source_egg_id ?? null) || $item->source_egg_id < 1 ||
            !is_string($item->source_image ?? null) || !preg_match('/^[^\p{C}\s]{1,512}$/uD', $item->source_image) ||
            !is_array($item->source_variables ?? null) || count($item->source_variables) > 100)
            throw new RuntimeException('A migration profile has an invalid source egg, image or variable list.');
        $names = [];
        foreach ($item->source_variables as $name) {
            if (!is_string($name) || !preg_match('/^[A-Za-z_][A-Za-z0-9_]{0,127}$/D', $name) || isset($names[$name]))
                throw new RuntimeException('Use unique environment variable names in each migration profile.');
            $names[$name] = true;
        }
        $binding = $item->source_egg_id . "\0" . $item->source_image;
        if (isset($profiles[$binding])) throw new RuntimeException('Use a unique mapping for each source egg and image.');
        $profiles[$binding] = [$item->source_egg_id, $item->source_image, array_keys($names)];
    }
    return $profiles;
}

function collectServerSettings(PDO $source, array $profiles): array
{
    $values = [];
    foreach ($profiles as [$eggId, $image, $names]) {
        if ($names === []) continue;
        $parameters = implode(', ', array_fill(0, count($names), '?'));
        $query = $source->prepare(
            "SELECT s.id AS server_id, ev.env_variable, COALESCE(sv.variable_value, ev.default_value) AS effective_value
             FROM servers s JOIN egg_variables ev ON ev.egg_id = s.egg_id
             LEFT JOIN server_variables sv ON sv.variable_id = ev.id AND sv.server_id = s.id
             WHERE s.egg_id = ? AND CAST(s.image AS BINARY) = CAST(? AS BINARY)
             AND CAST(ev.env_variable AS BINARY) IN ($parameters)"
        );
        $query->execute([$eggId, $image, ...$names]);
        while (($setting = $query->fetch(PDO::FETCH_ASSOC)) !== false) {
            $serverId = $setting['server_id'];
            $name = $setting['env_variable'];
            $value = $setting['effective_value'];
            if (!is_string($value) || strlen($value) > 16384 || isset($values[$serverId][$name]))
                throw new RuntimeException('An approved source variable has an invalid value or duplicate assignment.');
            $values[$serverId][$name] = $value;
        }
    }
    return $values;
}

function collectServers(PDO $source, ?array $settingsPlan = null): array
{
    $sourceSettings = $settingsPlan === null ? [] : collectServerSettings($source, $settingsPlan);
    $otherPorts = [];
    foreach ($source->query(
        'SELECT a.server_id, a.port FROM allocations a JOIN servers s ON s.id = a.server_id
         WHERE a.id <> s.allocation_id ORDER BY a.port'
    )->fetchAll(PDO::FETCH_ASSOC) as $allocation) {
        $otherPorts[$allocation['server_id']][] = (int) $allocation['port'];
    }
    $servers = [];
    foreach ($source->query(
        'SELECT s.id, s.uuid AS server_uuid, s.uuidShort AS identifier, s.node_id AS source_node_id, s.egg_id AS source_egg_id,
                s.name, s.description, s.image AS source_image, s.memory, s.cpu, s.disk, s.threads AS source_threads, u.uuid AS owner_uuid, a.port
         FROM servers s LEFT JOIN users u ON u.id = s.owner_id
         LEFT JOIN allocations a ON a.id = s.allocation_id AND a.server_id = s.id AND a.node_id = s.node_id
         ORDER BY s.id'
    )->fetchAll(PDO::FETCH_ASSOC) as $server) {
        if ($server['owner_uuid'] === null || $server['port'] === null)
            throw new RuntimeException('A source server has no owner UUID or valid primary allocation.');
        $server['target_node_id'] = '00000000-0000-0000-0000-000000000000';
        $server['approved_image'] = null;
        $server['cartridge_id'] = null;
        $server['variables'] = new stdClass();
        $server['named_ports'] = new stdClass();
        $server['target_cpu_cores'] = null;
        $server['secondary_ports'] = $otherPorts[$server['id']] ?? [];
        if ($settingsPlan !== null) $server['source_variables'] = (object) ($sourceSettings[$server['id']] ?? []);
        unset($server['id']);
        $servers[] = $server;
    }
    return ['schema_version' => $settingsPlan === null ? 1 : 2, 'servers' => $servers];
}

function collectBackupReceipts(PDO $source, ?string $serverUuid): array
{
    if ($serverUuid === null || !preg_match('/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iD', $serverUuid) ||
        $serverUuid === '00000000-0000-0000-0000-000000000000') {
        throw new RuntimeException('Provide a valid UUID for the source server.');
    }
    $serverUuid = strtolower($serverUuid);
    $statement = $source->prepare(
        'SELECT b.uuid AS backup_uuid, b.disk, b.bytes AS archive_bytes, b.checksum
         FROM backups b JOIN servers s ON s.id = b.server_id
         WHERE s.uuid = ? AND b.deleted_at IS NULL AND b.completed_at IS NOT NULL
               AND b.is_successful = 1 AND b.checksum IS NOT NULL
         ORDER BY b.id'
    );
    $statement->execute([$serverUuid]);
    return ['schema_version' => 1, 'server_uuid' => $serverUuid, 'backups' => $statement->fetchAll(PDO::FETCH_ASSOC)];
}

function collectSubusers(PDO $source): array
{
    $members = $source->query(
        'SELECT s.uuid AS server_uuid, u.uuid AS user_uuid, a.permissions
         FROM subusers a LEFT JOIN servers s ON s.id = a.server_id LEFT JOIN users u ON u.id = a.user_id
         ORDER BY a.server_id, a.user_id'
    )->fetchAll(PDO::FETCH_ASSOC);
    foreach ($members as &$member) {
        if ($member['server_uuid'] === null || $member['user_uuid'] === null)
            throw new RuntimeException('A source subuser has no server or user UUID.');
        $permissions = $member['permissions'] === null ? [] : json_decode($member['permissions'], false, 8, JSON_THROW_ON_ERROR);
        if (!is_array($permissions)) throw new RuntimeException('A source subuser has an invalid permission list.');
        $member['permissions'] = $permissions;
    }
    unset($member);
    return ['schema_version' => 1, 'members' => $members];
}

function inventoryMain(?string $accountMode, ?string $operand = null): int
{
    if (!extension_loaded('pdo_mysql')) {
        fwrite(STDERR, "Install PHP's pdo_mysql extension to collect inventory.\n");
        return 1;
    }

    $dsn = getenv('PTERO_INVENTORY_DSN');
    $user = getenv('PTERO_INVENTORY_USER');
    $passwordFile = getenv('PTERO_INVENTORY_PASSWORD_FILE');
    $sslCa = getenv('PTERO_INVENTORY_SSL_CA');
    if (!$dsn || !str_starts_with($dsn, 'mysql:') || !$user || !$passwordFile || !is_readable($passwordFile)) {
        fwrite(STDERR, "Set the MySQL DSN, SELECT-only user and password file for a restored Pterodactyl database.\n");
        return 1;
    }

    $options = [PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION, PDO::ATTR_EMULATE_PREPARES => false];
    if (!str_starts_with($dsn, 'mysql:unix_socket=')) {
        if (!$sslCa || !is_readable($sslCa)) {
            fwrite(STDERR, "Set a trusted CA file when connecting over TCP.\n");
            return 1;
        }
        if (class_exists(Pdo\Mysql::class, false)) {
            $options[Pdo\Mysql::ATTR_SSL_CA] = $sslCa;
            $options[Pdo\Mysql::ATTR_SSL_VERIFY_SERVER_CERT] = true;
        } else {
            $options[PDO::MYSQL_ATTR_SSL_CA] = $sslCa;
            $options[PDO::MYSQL_ATTR_SSL_VERIFY_SERVER_CERT] = true;
        }
    }

    try {
        $settingsPlan = $accountMode === '--servers-with-settings' ? readServerSettingsPlan($operand ?? '') : null;
        $encrypter = null;
        if ($accountMode === '--accounts-with-totp' || $accountMode === '--administrators') {
            if (!extension_loaded('mbstring') || !extension_loaded('openssl')) {
                throw new RuntimeException('Install the mbstring and openssl PHP extensions to export TOTP accounts.');
            }
            $autoload = getenv('PTERO_INVENTORY_LARAVEL_AUTOLOAD');
            $appKeyFile = getenv('PTERO_INVENTORY_APP_KEY_FILE');
            if (!$autoload || !is_readable($autoload) || !$appKeyFile || !is_readable($appKeyFile)) {
                throw new RuntimeException('Set the trusted Pterodactyl Laravel autoloader and source APP_KEY file.');
            }
            require_once $autoload;
            $appKey = trim((string) file_get_contents($appKeyFile));
            $key = str_starts_with($appKey, 'base64:') ? base64_decode(substr($appKey, 7), true) : $appKey;
            if (!is_string($key) || strlen($key) !== 32) throw new RuntimeException('Invalid source encryption key.');
            $encrypter = new Illuminate\Encryption\Encrypter($key, 'AES-256-CBC');
            unset($appKey, $key);
        }
        $secret = file_get_contents($passwordFile);
        if ($secret === false) throw new RuntimeException('Could not read the password file.');
        $source = new PDO($dsn, $user, rtrim($secret, "\r\n"), $options);
        unset($secret);
        $source->exec('SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY');
        $source->beginTransaction();
        try {
            if ($accountMode === '--servers' || $accountMode === '--servers-with-settings') {
                $inventory = collectServers($source, $settingsPlan);
            } elseif ($accountMode === '--schedules') {
                $inventory = collectSchedules($source);
            } elseif ($accountMode === '--backup-receipts') {
                $inventory = collectBackupReceipts($source, $operand);
            } elseif ($accountMode === '--subusers') {
                $inventory = collectSubusers($source);
            } elseif ($accountMode !== null) {
                $accountQuery = 'SELECT uuid, username, email, name_first, name_last, password, use_totp, root_admin';
                if ($encrypter !== null) $accountQuery .= ', totp_secret';
                $accountQuery .= ' FROM users';
                if ($accountMode === '--administrators') {
                    $ids = array_map('trim', explode(',', getenv('PTERO_INVENTORY_ADMINISTRATOR_IDS') ?: ''));
                    if (count($ids) > 100 || count(array_unique(array_map('strtolower', $ids))) !== count($ids))
                        throw new RuntimeException('Provide up to 100 unique approved administrator UUIDs.');
                    foreach ($ids as $id) {
                        if (!preg_match('/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iD', $id) || $id === '00000000-0000-0000-0000-000000000000')
                            throw new RuntimeException('Provide valid approved administrator UUIDs.');
                    }
                    $placeholders = implode(',', array_fill(0, count($ids), '?'));
                    $statement = $source->prepare($accountQuery . " WHERE root_admin = 1 AND use_totp = 1 AND uuid IN ($placeholders) ORDER BY id");
                    $statement->execute($ids);
                    $accounts = $statement->fetchAll(PDO::FETCH_ASSOC);
                    if (count($accounts) !== count($ids))
                        throw new RuntimeException('An approved administrator is missing or has no active TOTP.');
                } else {
                    $accountQuery .= ' WHERE root_admin = 0';
                    if ($encrypter === null) $accountQuery .= ' AND use_totp = 0';
                    $accounts = $source->query($accountQuery . ' ORDER BY id')->fetchAll(PDO::FETCH_ASSOC);
                }
                foreach ($accounts as &$account) {
                    $account['root_admin'] = (bool) $account['root_admin'];
                    $account['use_totp'] = (bool) $account['use_totp'];
                    if ($account['use_totp']) {
                        $account['totp_secret'] = decryptTotpSecret((string) $account['totp_secret'], $encrypter);
                    } else {
                        unset($account['totp_secret']);
                    }
                }
                unset($account);
                $inventory = ['schema_version' => 1, 'users' => $accounts];
            } else {
                $inventory = collectInventory($source);
            }
        } finally {
            $source->rollBack();
        }
        $result = json_encode($inventory, JSON_THROW_ON_ERROR | JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES) . "\n";
        if ($settingsPlan !== null && strlen($result) > 16 * 1024 * 1024)
            throw new RuntimeException('The server configuration export exceeds the 16 MiB import limit. Use a smaller restored inventory.');
        fwrite(STDOUT, $result);
        return 0;
    } catch (PDOException | JsonException) {
        fwrite(STDERR, "Could not collect inventory. Verify the connection and snapshot encoding.\n");
    } catch (RuntimeException $error) {
        fwrite(STDERR, $error->getMessage() . "\n");
    }
    return 1;
}

if (PHP_SAPI === 'cli' && realpath($_SERVER['SCRIPT_FILENAME'] ?? '') === __FILE__) {
    $mode = $argv[1] ?? null;
    $valid = in_array($mode, ['--backup-receipts', '--servers-with-settings'], true) ? $argc === 3 : $argc === 1 || ($argc === 2 &&
        in_array($mode, ['--accounts', '--accounts-with-totp', '--administrators', '--schedules', '--servers', '--subusers'], true));
    if (!$valid) {
        fwrite(STDERR, "Usage: php inventory.php [--accounts | --accounts-with-totp | --administrators | --schedules | --servers | --servers-with-settings <mapping.json> | --subusers | --backup-receipts <server-uuid>]\n");
        exit(1);
    }
    exit(inventoryMain($mode, $argv[2] ?? null));
}
