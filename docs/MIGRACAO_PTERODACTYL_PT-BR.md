# Preparação da migração Pterodactyl

## Inventário da origem

Execute em uma cópia restaurada do banco do painel antigo com uma conta que só tenha `SELECT`. O coletor requer PHP CLI com `pdo_mysql`. O JSON contém a topologia da instalação. Guarde-o fora do repositório com acesso restrito.

```sh
umask 077
export PTERO_INVENTORY_DSN='mysql:unix_socket=/run/mysqld/mysqld.sock;dbname=pterodactyl_restore;charset=utf8mb4'
export PTERO_INVENTORY_USER='arcadenode_inventory'
export PTERO_INVENTORY_PASSWORD_FILE='/run/secrets/ptero_inventory_password'
php scripts/pterodactyl-migration/inventory.php > /caminho/privado/ptero-inventory.json
```

Para acessar por TCP, configure uma DSN com `host`, `port`, `dbname` e `charset` e indique a CA confiável em `PTERO_INVENTORY_SSL_CA`. A consulta usa uma transação somente leitura com `REPEATABLE READ`. O inventário relaciona contas, nodes, eggs, servidores, alocações, subusuários, agendas, tarefas, backups e bancos de jogos, usando uma lista explícita de campos permitidos. [MariaDB](https://mariadb.com/docs/server/reference/sql-statements/transactions/set-transaction) · [MySQL](https://dev.mysql.com/doc/refman/8.4/en/set-transaction.html)

`panel_status` é o estado registrado no painel. `live_runtime_checked` indica se houve verificação do runtime no node. O coletor SQL retorna `false`. Use os UUIDs para relacionar esse inventário aos containers, volumes, backups e estado observado separadamente em cada node. Contas com `use_totp` precisam manter o segundo fator durante a migração.

Analise o inventário com o preflight em C# para localizar vínculos quebrados, conflitos de portas por node e limites de recursos que exigem mapeamento. O relatório também conta contas com TOTP, administradores de origem e dependências por servidor.

```sh
dotnet run --project scripts/pterodactyl-migration/Preflight.csproj -- /caminho/privado/ptero-inventory.json
```

O código `0` indica que as verificações do inventário passaram, `2` indica que o relatório contém pendências e `1` indica entrada inválida ou indisponível. A disponibilidade dos nodes, o estado ao vivo e a integridade de volumes e backups precisam ser verificados na infraestrutura.

A autenticação aceita hashes bcrypt do PHP (`$2y$`) com custo de 4 a 14. Quem já tem uma senha legada curta pode usá-la para entrar e trocá-la por uma senha que atenda à política atual.

## Importação de contas comuns

Use a mesma cópia restaurada para exportar as contas comuns. A opção `--accounts` inclui hashes de senha e filtra administradores de origem e usuários com TOTP. Guarde o arquivo como segredo e configure a identidade de manutenção do ArcadeNode para acessar o SQL Server de destino. Use um arquivo novo para a exportação.

```sh
(umask 077; set -C; php scripts/pterodactyl-migration/inventory.php --accounts > /caminho/privado/ptero-accounts.json)
export Migration__PterodactylAccountsFile='/caminho/privado/ptero-accounts.json'
dotnet run --project backend/ServerPanel.API -- --plan-pterodactyl-accounts
dotnet run --project backend/ServerPanel.API -- --import-pterodactyl-accounts
```

O plano registra contagens e motivos por posição no arquivo, sem expor nomes, e-mails ou hashes. O importador preserva UUIDs e senhas e grava o lote em uma transação quando todas as contas passam na validação. Conflitos com contas existentes exigem revisão. O inventário principal continua listando administradores para revisão separada.

Para preservar TOTP nas contas comuns, aplique a migration `ProtectTotpAccounts`. O Compose monta o volume privado `data_protection_keys` no serviço da API e no serviço de manutenção. Em instalações fora do Compose ou com réplicas em hosts diferentes, configure `Security__DataProtectionKeyDirectory` para o mesmo armazenamento persistente e privado em todos os processos. A restauração do banco precisa incluir as chaves correspondentes.

Em Compose, dê ao UID da API acesso de leitura ao manifesto privado e execute o planejamento com o serviço de manutenção.

```sh
docker compose --profile maintenance run --rm \
  --volume /caminho/privado/ptero-accounts.json:/run/pterodactyl/accounts.json:ro \
  --env Migration__PterodactylAccountsFile=/run/pterodactyl/accounts.json \
  migrate --plan-pterodactyl-accounts
```

Quando o plano estiver sem conflitos, repita o comando com `migrate --import-pterodactyl-accounts`.

Use `--accounts-with-totp` no lugar de `--accounts`. O PHP CLI precisa das extensões `mbstring` e `openssl` para esse modo e para `--administrators`. Defina `PTERO_INVENTORY_LARAVEL_AUTOLOAD` para o `vendor/autoload.php` da instalação confiável de origem e `PTERO_INVENTORY_APP_KEY_FILE` para um arquivo privado com apenas a chave original. O manifesto temporário contém hashes e segredos TOTP em texto legível. Mantenha-o em armazenamento restrito e criptografado durante a transferência e elimine a cópia temporária conforme o procedimento de gestão de segredos. O ArcadeNode protege os segredos ao gravar. Os códigos de recuperação do painel antigo permanecem na origem.

Para migrar administradores, revise seus UUIDs no inventário e exporte um lote separado com `--administrators`. Configure a lista de UUIDs aprovados, separados por vírgulas, em `PTERO_INVENTORY_ADMINISTRATOR_IDS` na origem e em `Migration__PterodactylAdministratorIds` no ArcadeNode. Use os mesmos comandos de simulação e importação com o manifesto administrativo. Cada conta precisa de TOTP ativo no painel antigo e entra com o papel `Admin` no ArcadeNode. A identidade `SuperAdmin` de inicialização continua sendo gerenciada pelo ArcadeNode.

Antes de inicializar um destino novo, escolha um nome e e-mail livres no inventário para o SuperAdmin do ArcadeNode. Isso preserva logins legados como `admin`. Defina `ARCADENODE_BOOTSTRAP_USERNAME` e `ARCADENODE_BOOTSTRAP_EMAIL` no Compose, ou `Seed__AdminUsername` e `Seed__AdminEmail` ao executar `--migrate` diretamente. A configuração é aplicada durante a criação da conta inicial. [Guia de implantação](../deploy/README.md)

## Cadastro dos servidores de destino

Exporte os metadados da mesma cópia restaurada. Importe as contas primeiro. Preencha `target_node_id` com o UUID do node no ArcadeNode e `approved_image` com uma imagem gerenciada aprovada, equivalente ao ambiente de origem. O export preserva os identificadores e os limites antigos como base para revisão.

```sh
(umask 077; set -C; php scripts/pterodactyl-migration/inventory.php --servers > /caminho/privado/ptero-servers.json)
export Migration__PterodactylServersFile='/caminho/privado/ptero-servers.json'
dotnet run --project backend/ServerPanel.API -- --plan-pterodactyl-servers
dotnet run --project backend/ServerPanel.API -- --import-pterodactyl-servers
```

O cadastro reserva a porta principal, até 16 portas secundárias e a capacidade de RAM e disco. Ele também preserva o UUID, o identificador curto e o proprietário. As entradas ficam suspensas, sem container. A etapa opera no banco de destino. O container restaurado publica as portas no endereço de jogos cadastrado para o node, tanto em TCP quanto em UDP. Se a origem usava IPs diferentes para o mesmo número de porta, ajuste o mapeamento antes de importar.

Quando `source_threads` indicar afinidade na origem, preencha `target_cpu_cores` com os IDs lógicos equivalentes no node de destino. Por exemplo, `[1, 3]`. O plano rejeita afinidade ausente, núcleos fora de `AllowedCpuCores` ou uma quota que exceda os núcleos selecionados. Aplique a migration `ServerCpuAffinity` antes de usar esse mapeamento.

O planejamento usa os mesmos limites de reserva da criação de servidores. A RAM e o disco consideram o total e a sobrealocação do node. Quando `CpuCapacityPercent` está configurado no JSON privado, a soma das quotas de CPU também é limitada. Por exemplo, `800` representa oito CPUs lógicas. Se uma linha exceder um desses saldos, o relatório indica `destination_capacity_exceeded` e o lote permanece sem gravação. [Capacidade no cadastro do node](../deploy/README.md#nodes-imagens-e-quotas)

Para preservar parâmetros de execução como versão do jogo ou nome do mundo, indique `cartridge_id` e preencha `variables` com os valores equivalentes do cartridge. O planejador exige que o cartridge e `approved_image` apontem para a mesma imagem aprovada e rejeita variáveis fora do perfil. Valores ausentes usam os padrões do cartridge. O export de metadados deixa o objeto `variables` vazio. Confira a configuração do jogo antigo ao preencher o mapeamento e mantenha o manifesto em armazenamento privado.

Use `named_ports` para ligar portas publicadas a funções do cartridge. Por exemplo, no Minecraft, `"named_ports": { "query": 25567 }` configura `QUERY_PORT` quando 25567 for a porta principal ou constar em `secondary_ports`. O planejador recusa nomes desconhecidos e portas sem reserva. Para que o Minecraft aplique `QUERY_PORT`, use a imagem gerenciada reconstruída com o entrypoint atualizado.

### Importar as configurações de cada servidor

Para aplicar um perfil a vários servidores, importe e aprove primeiro a egg no [catálogo](#catálogo-de-eggs). Copie o UUID mostrado ao lado da egg. Use os IDs numéricos e as imagens exatas da origem, conforme o inventário da instalação. O exemplo abaixo usa a egg Vanilla e reserva as variáveis que o perfil aprovado já associa a `MC_VERSION` e `SERVER_JAR`.

```json
{
  "schema_version": 1,
  "nodes": [{ "source_node_id": 4, "target_node_id": "96bcdf0c-a92c-4de3-a626-7bc7e5ba3880" }],
  "profiles": [{
    "source_egg_id": 7,
    "source_image": "ghcr.io/pterodactyl/yolks:java_21",
    "target_egg_id": "bde71054-a12e-42ce-8551-ce1dc6646d3f",
    "source_variables": ["VANILLA_VERSION", "SERVER_JARFILE"]
  }]
}
```

Use o mesmo mapeamento no exportador e no destino. O exportador usa o valor gravado no servidor ou o padrão da egg na cópia do banco. Ele consulta apenas as variáveis selecionadas para aquela egg e imagem. Inclua todos os nomes de origem que constam em `VariableMap` no perfil do destino. O manifesto pode conter valores privados quando você seleciona variáveis privadas. Guarde ambos os arquivos fora do repositório. Simule a migração com antecedência. Antes de aplicar, bloqueie alterações nas configurações da origem e exporte novamente a partir da cópia final do banco.

```sh
mapping='/caminho/privado/server-mapping.json'
(umask 077; set -C; php scripts/pterodactyl-migration/inventory.php --servers-with-settings "$mapping" > /caminho/privado/ptero-servers-settings.json)
export Migration__PterodactylMappingsFile="$mapping"
export Migration__PterodactylServersFile='/caminho/privado/ptero-servers-settings.json'
dotnet run --project backend/ServerPanel.API -- --plan-pterodactyl-servers
dotnet run --project backend/ServerPanel.API -- --import-pterodactyl-servers
```

A instalação via Compose usa o serviço de manutenção com o catálogo e a configuração de nodes da API. Dê ao UID da API acesso de leitura aos dois manifests privados e rode a simulação.

```sh
docker compose --profile maintenance run --rm \
  --volume /caminho/privado/server-mapping.json:/run/pterodactyl/mapping.json:ro \
  --volume /caminho/privado/ptero-servers-settings.json:/run/pterodactyl/servers.json:ro \
  --env Migration__PterodactylMappingsFile=/run/pterodactyl/mapping.json \
  --env Migration__PterodactylServersFile=/run/pterodactyl/servers.json \
  migrate --plan-pterodactyl-servers
```

Quando o plano passar, repita o comando com `--import-pterodactyl-servers`.

A simulação lista o node e a egg de destino por UUID e a quantidade de valores preservados. Você pode indicar outro `target_node_id` por servidor, ajustar `named_ports` e `target_cpu_cores` e complementar `variables` com opções do cartridge que o perfil não gerencia. Variáveis ausentes, divergências de imagem ou perfis indisponíveis bloqueiam a gravação do lote. O importador mantém os servidores suspensos e vincula cada um à egg aprovada. Após gravar, o mesmo manifesto pode ser reaplicado quando todos os dados coincidem.

### Verificação do volume

Faça a cópia inicial em um volume separado no destino enquanto a origem ainda atende jogadores. Na janela final, salve o jogo, pare o container na origem e bloqueie novas escritas por SFTP, tarefas e serviços externos. Preserve a origem para recuperação. Sincronize os arquivos alterados para o destino e guarde os relatórios fora dos volumes.

Para copiar por SSH, defina `source_volume` como o diretório de dados na origem, `destination_login` como `usuario@host` e `volume_destino` como o caminho absoluto de um volume exclusivo para a migração. Use uma identidade SSH autorizada nos dois nodes. O primeiro comando copia dados, permissões e horários. Depois de parar o jogo e bloquear as escritas, rode o segundo comando para revisar as diferenças. Remova `--dry-run` após conferir a lista. Nessa etapa o `rsync` também remove do volume de migração os arquivos já removidos na origem. [Manual do rsync](https://download.samba.org/pub/rsync/rsync.1)

```sh
rsync -rlptxs --safe-links -e ssh -- "$source_volume/" "$destination_login:$volume_destino/"
rsync -rlptxs --safe-links --checksum --delete-delay --dry-run --itemize-changes \
  -e ssh -- "$source_volume/" "$destination_login:$volume_destino/"
```

Para o volume definitivo do driver ArcadeNode, execute também a sincronização final com um receptor que grave cada arquivo como UID/GID 10001. Em uma cópia local entre a área temporária e o volume definitivo, um operador privilegiado pode acrescentar `--chown=10001:10001` ao `rsync`. Confira a opção no `--dry-run` junto com as remoções esperadas antes de aplicar. Uma sincronização posterior pode substituir arquivos, portanto mantenha o mapeamento em cada passada. [Driver de quota XFS](../deploy/quota/README.md) · [Mapeamento de dono no rsync](https://github.com/RsyncProject/rsync/blob/master/rsync.1.md)

Execute `capture-pterodactyl-volume` no node Linux da origem. O processo consulta o Docker local pelo UUID, identifica o bind `/home/container`, verifica que o mesmo container permanece parado e calcula o SHA-256 da árvore. A ferramenta recusa montagens adicionais dentro dos dados do jogo. Esse caso exige tratar cada montagem antes de preparar a migração. Para Docker rootless, configure o caminho absoluto do socket local em `PTERODACTYL_DOCKER_SOCKET`. [Configuração de containers no Wings](https://github.com/pterodactyl/wings/blob/develop/environment/docker/container.go)

```sh
dotnet run --project scripts/pterodactyl-migration/Preflight.csproj -- capture-pterodactyl-volume "$server_uuid" > source-volume.json
```

No destino, instale a publicação Linux do `Preflight` em um diretório de ferramentas acessível ao UID 10001 e gravável apenas pelo operador. Defina `tools_dir` com o caminho absoluto desse diretório. Use `linux-arm64` para um node ARM64 e escolha `linux-musl-x64` ou `linux-musl-arm64` em Alpine. Em uma sessão de operador com acesso privado ao recibo de origem, rode a verificação com o mesmo UID e GID do jogo. O shell abre o recibo com as permissões do operador e passa o JSON ao CLI pela entrada padrão. Se o UID do jogo não conseguir atravessar uma pasta ou ler um arquivo, a verificação falha. [Manual do setpriv](https://man7.org/linux/man-pages/man1/setpriv.1.html)

```sh
dotnet publish scripts/pterodactyl-migration/Preflight.csproj -c Release -r linux-x64 --self-contained true -o "$tools_dir"
preflight="$tools_dir/Preflight"
setpriv --reuid=10001 --regid=10001 --clear-groups -- "$preflight" \
  verify-volume "$server_uuid" - "$volume_destino" < source-volume.json > verified-volume.json
```

### Recuperar um backup do ArcadeNode

Com a recuperação habilitada no node, selecione **Preparar restauração** na aba **Backups**. Aguarde **Cópia preparada no node** e use o diretório `data` da recuperação como origem para `capture-volume`. [Configuração do node](ARQUIVOS_NODES_PT-BR.md#preparação-da-restauração)

Use o TAR/GZip criado pelo ArcadeNode e o SHA-256 registrado para ele no painel. Escolha um diretório pai privado e separado dos volumes em produção. Execute com o UID que deverá acessar os arquivos recuperados.

```sh
dotnet run --project scripts/pterodactyl-migration/Preflight.csproj -- restore-backup "$server_uuid" "$backup_tar_gz" "$sha256_do_painel" "$novo_diretorio"
dotnet run --project scripts/pterodactyl-migration/Preflight.csproj -- capture-volume "$server_uuid" "$novo_diretorio" > recovered-volume.json
```

O primeiro comando verifica o arquivo e publica a cópia apenas se a extração terminar com sucesso e o destino estiver livre. A saída registra a quantidade de arquivos, pastas e links, o total de bytes e o SHA-256 do arquivo. O segundo comando produz o relatório da árvore para validar a cópia em um volume definitivo. Os backups novos incluem os horários de modificação e as permissões POSIX básicas dos arquivos regulares e diretórios. Os links internos são recriados em Linux com o mesmo caminho relativo. A propriedade dos arquivos segue o UID do operador. A cópia publicada continua separada até a troca controlada do volume pelo operador.

O recibo Linux de versão 3 compara caminhos, diretórios vazios, tamanhos, SHA-256, permissões e links simbólicos relativos. Ele registra o texto do destino de cada link no digest e valida que o caminho aponta diretamente para um arquivo regular ou uma pasta no mesmo volume. A captura recusa links absolutos, encadeados, quebrados ou que cruzem o limite do volume. O `rsync` acima preserva links relativos dentro da árvore. O verificador continua compatível com recibos Linux de versão 2, que exigem árvores sem links. Rode a captura e a verificação em Linux. O código `0` indica correspondência, `2` indica diferença e `1` indica erro de leitura ou entrada inválida. Confira também UID, GID, ACLs, o diretório raiz do volume, bancos de jogos e consistência dos saves na homologação. [Resolução de caminhos no Linux](https://man7.org/linux/man-pages/man2/openat2.2.html) · [Links no rsync](https://download.samba.org/pub/rsync/rsync.1#opt--safe-links)

### Recuperar um backup do Pterodactyl

Exporte os recibos do servidor na mesma cópia restaurada do banco. Use o UUID completo do servidor e do backup. Para obter o arquivo, use o download autenticado no Pterodactyl ou uma cópia autorizada do armazenamento de origem. O Wings guarda backups locais como `<UUID do backup>.tar.gz` no diretório configurado. Transfira o recibo e o arquivo por um canal autenticado. [Formato e checksum do Wings](https://github.com/pterodactyl/wings/blob/develop/server/backup/backup.go)

```sh
(umask 077; set -C; php scripts/pterodactyl-migration/inventory.php --backup-receipts "$server_uuid" > /caminho/privado/ptero-backup-receipts.json)
dotnet run --project scripts/pterodactyl-migration/Preflight.csproj -- restore-pterodactyl-backup "$server_uuid" "$backup_uuid" /caminho/privado/ptero-backup-receipts.json "$backup_tar_gz" "$novo_diretorio"
```

Execute a recuperação no node de destino com o UID do jogo e use um diretório novo sob um pai privado. O comando confere o tamanho e o checksum do banco, calcula o SHA-256 do arquivo transferido e publica uma cópia separada. Ele recria pastas implícitas no TAR e preserva os horários e permissões dos arquivos regulares e diretórios. Em Linux, links simbólicos relativos são recriados se apontarem diretamente para um arquivo ou diretório restaurado. Links absolutos, quebrados, encadeados, hard links e arquivos especiais interrompem a recuperação. O limite é de 68 GiB comprimidos, 64 GiB de arquivos e 250 mil itens. [Código de geração do TAR no Wings](https://github.com/pterodactyl/wings/blob/develop/server/filesystem/archive.go)

Use essa cópia para recuperação ou para adiantar a transferência dos dados. Para liberar a migração de um servidor ativo, faça a sincronização final com a origem parada e valide a árvore contra o recibo de `capture-pterodactyl-volume`. Um backup pode ter exclusões e ter sido criado antes das últimas gravações do jogo.

### Preparação do container restaurado

Aplique as migrations `TrackStagedServerRestores` e `GatePterodactylCutover`. No node de destino, use o driver de quota cadastrado para preparar o volume definitivo `arcadenode-<uuid sem hífens>-data`. Configure a quota em bytes conforme a reserva e os rótulos `panel.server.id` com o UUID com hífens e `panel.restore.sha256` com o SHA-256 em maiúsculas da origem. Aponte `verify-volume` para os dados desse volume definitivo.

Abra **Administração → Migrações**, selecione o servidor pelo nome ou UUID e envie `verified-volume.json` em **Preparar container**. O mesmo relatório pode ser enviado a `POST /api/admin/migrations/servers/{id}/prepare-restored` com uma sessão administrativa. O ArcadeNode exige recibos de versão 2 ou 3 para preparar novos containers, confere o volume registrado no Docker e cria o destino parado. A suspensão fica ativa até a liberação da migração. Para um container já preparado com recibo de versão 1, gere os dois relatórios finais com o CLI atualizado usando o recibo de origem existente.

Para Minecraft Java, reconstrua a imagem gerenciada e monte o volume restaurado em `/home/minecraft`. O JAR, `eula.txt` e `server.properties` precisam estar na raiz do volume, junto com os saves e plugins. O modo de restauração abre o JAR local e mantém os arquivos existentes. Confira a EULA, as portas e a senha de RCON, quando habilitado, antes de capturar o snapshot final.

Para Minecraft Bedrock, reconstrua a imagem gerenciada e monte o volume em `/home/bedrock`. No layout Pterodactyl, preserve `bedrock_server` executável, `server.properties` e `worlds/` na raiz. No layout gerenciado, preserve esses itens em `server/`. O diretório `server/worlds` pode ser um link relativo para `../data/worlds`. O boot restaurado usa a distribuição existente e preserva mundos, configurações, listas e packs. A porta em `server-port` deve corresponder à porta principal. Para publicar a porta de `server-portv6`, inclua-a em `secondary_ports` e associe-a a `named_ports.ipv6`. Sem vínculo explícito, o boot aceita a configuração padrão de IPv6 na porta 19133.

Acrescente `--minecraft-bedrock` após o diretório de destino em `verify-volume` na preparação e na liberação. A ferramenta confere o executável, o diretório de mundos e as duas portas. A API compara as portas do relatório com a configuração e as reservas do servidor antes de prosseguir.

Em instalações gerenciadas novas, `VERSION=LATEST` usa a versão estável Linux do catálogo oficial. Um número de quatro partes fixa o pacote solicitado. As reinicializações e atualizações preservam o `server.properties`, o mundo, a lista de acesso e as permissões existentes. Se o catálogo ou o pacote ficar indisponível, `LATEST` pode iniciar a última instalação concluída. O modo restaurado inicia os arquivos copiados sem buscar atualização. [Catálogo oficial de downloads](https://net-secondary.web.minecraft-services.net/api/v1.0/download/links)

Para Project Zomboid, reconstrua a imagem gerenciada e monte o volume em `/home/pzserver`. No layout da egg Pterodactyl, mantenha `ProjectZomboid64`, `ProjectZomboid64.json` e `jre64/` na raiz e preserve `.cache/`. O layout gerenciado, com a instalação em `server/` e o estado do jogo em `Zomboid/`, também é aceito. O diretório de estado deve conter `Server/<nome>.ini`, `Saves/Multiplayer/<nome>/` e `db/<nome>.db`. Preserve o nome original em `variables.SERVER_NAME` e publique a porta direta indicada em `named_ports.steam`. Se a origem iniciava com `-nosteam`, defina `variables.NO_STEAM` como `true` no manifesto. O padrão é `false`, que mantém o modo Steam. A restauração inicia o build existente com o banco de jogadores preservado. Ao iniciar, o entrypoint aplica o teto de memória em `ProjectZomboid64.json`. [Egg oficial do Pterodactyl](https://github.com/pterodactyl/game-eggs/blob/main/project_zomboid/egg-project-zomboid.json)

No layout gerenciado, cada pasta `Saves`, `Server`, `Logs` e `db` em `Zomboid/` também pode ser um link relativo para a pasta correspondente em `data/`, como `Zomboid/Saves` apontando para `../data/Saves`. Se uma instalação antiga ainda usa links absolutos para o mesmo `data/`, revise e converta esses links para relativos com a origem parada antes da captura final. Preserve as pastas de dados e seu conteúdo.

Para o Zomboid, acrescente `--project-zomboid "$server_name"` após o diretório de destino em `verify-volume`, tanto na preparação quanto na liberação. Use o valor de `variables.SERVER_NAME` em `server_name`. A ferramenta identifica o layout, confere se o UID do jogo pode executar o launcher, encontra o save, lê o cabeçalho e verifica o tamanho das páginas do SQLite. A conferência de execução usa `faccessat2` no Linux. O relatório inclui o nome e as portas por layout. O painel compara esses dados com a reserva e exige a porta UDP direta adicional. Quando o RCON está habilitado, confira a porta configurada e mantenha a publicação apenas se prevista na migração. A senha permanece no arquivo de configuração. [Formato do SQLite](https://www.sqlite.org/fileformat2.html) · [Permissões de execução no Linux](https://man7.org/linux/man-pages/man2/access.2.html)

Ao aprovar a egg para novas instalações, o perfil pode mapear `SRCDS_BETAID` para `BETA_BRANCH` e `AUTO_UPDATE` para `AUTO_UPDATE`. A instalação usa a branch solicitada. Reinícios mantêm o build instalado por padrão. A migração conserva a autenticação no banco de jogadores copiado, e a senha inicial do administrador é exigida apenas para novos servidores. Preserve o build de origem durante a migração de saves do Build 41. [Compatibilidade de saves informada pelo estúdio](https://projectzomboid.com/blog/news/2026/07/build-42-stable-plans/)

Para Arma Reforger, reconstrua a imagem gerenciada e monte o volume em `/home/reforger`. No layout da egg oficial, preserve `ArmaReforgerServer`, `config.json`, `profile/` e `addons/` na raiz. No layout gerenciado, preserve a instalação em `server/`, o JSON em `data/configs/server.json` e as pastas `data/profile/` e `data/addons/`. A porta do jogo deve corresponder à principal do manifesto. Se A2S ou RCON estiverem habilitados, inclua suas portas em `secondary_ports` e use os vínculos `named_ports.a2s` e `named_ports.rcon`. Confira também o endereço público anunciado antes de capturar o snapshot final. Para novas instalações, o ArcadeNode reserva a porta A2S a partir da 17777. [Configuração do Arma Reforger](https://community.bistudio.com/wiki/Arma_Reforger%3AServer_Config) · [Egg oficial do Pterodactyl](https://github.com/pterodactyl/game-eggs/blob/main/arma/arma_reforger/egg-arma-reforger.json)

Acrescente `--arma-reforger` após o diretório de destino em `verify-volume` na preparação e na liberação. A ferramenta confere o executável, perfil, addons e configuração. O relatório leva as portas verificadas ao painel. As senhas continuam no volume. Se um destino Bedrock ou Reforger já foi preparado, gere novamente a verificação com a opção correspondente antes de liberar o container.

O painel exige o rótulo `arcadenode.restore=1` na imagem aprovada para preparar um container restaurado. Os Dockerfiles de Minecraft Java, Bedrock, Project Zomboid e Arma Reforger incluem esse rótulo.

### Liberação e rollback

Após preparar o container e com os dois volumes protegidos contra gravações, rode `confirm-pterodactyl-source` no node de origem e `verify-volume` como UID/GID 10001 no node de destino. O primeiro comando relê o volume e confirma o mesmo container parado antes e depois da leitura. A opção `--writes-fenced` registra a confirmação do operador sobre os demais caminhos de escrita. O segundo comando relê os dados do volume definitivo com as permissões do jogo e compara com o mesmo recibo de origem. Transfira os relatórios por um canal autenticado.

```sh
dotnet run --project scripts/pterodactyl-migration/Preflight.csproj -- confirm-pterodactyl-source "$server_uuid" source-volume.json --writes-fenced > source-fence.json
setpriv --reuid=10001 --regid=10001 --clear-groups -- "$preflight" \
  verify-volume "$server_uuid" - "$volume_destino" < source-volume.json > final-volume.json
```

Envie `source-fence.json` e `final-volume.json` pela opção **Liberar destino** em **Migrações** dentro de 15 minutos. Para usar `POST /api/admin/migrations/servers/{id}/release-prepared`, envie os campos de `source-fence.json` com o relatório final inteiro na propriedade `destination_verification`. O ArcadeNode confere os recibos e se o destino continua suspenso e parado no Docker, registra o administrador e libera o servidor para uma inicialização explícita. Confira a saúde do destino antes de apontar os jogadores para ele. Preserve a origem e os recibos durante a homologação.

Se a origem mudou depois da captura, mantenha o destino suspenso, capture um novo recibo da origem parada, sincronize os dados no mesmo volume definitivo e envie a verificação atualizada em **Reenviar verificação**. O ArcadeNode atualiza o digest registrado enquanto o container preparado está parado. Depois, gere novamente os dois relatórios de liberação. O rótulo `panel.restore.sha256` do volume registra a preparação inicial no Docker.

Para retornar à origem antes de qualquer gravação no destino, mantenha o ArcadeNode suspenso antes de liberar o acesso no Pterodactyl. Após a primeira gravação no destino, o retorno requer sincronização e verificação no sentido inverso com ambos os lados sem escritores.

### Bancos externos usados pelos jogos

O inventário associa cada banco externo ao UUID do jogo e ao host de origem. Se o jogo usa MariaDB ou MySQL fora do volume, inclua esse banco no mesmo plano de virada. Se o host for mantido, valide a conexão do node de destino com o banco antes da janela. Se o host for trocado, copie o schema e os dados para uma instância de teste, revise usuários, grants e os definidores de views, triggers, rotinas e eventos. Crie uma credencial específica para o jogo no destino e atualize o endereço e a credencial no arquivo de configuração restaurado ou na configuração privada usada pelo jogo. Preserve uma cópia verificada dos dados e registre as alterações de configuração.

Exemplo de ensaio MariaDB com schema InnoDB e destino vazio. Os dois arquivos de opções privados contêm o usuário, a autenticação e os parâmetros de conexão dos respectivos hosts. Use clientes compatíveis com os dois servidores.

```sh
umask 077
set -C
mariadb-dump --defaults-extra-file=/run/secrets/game-db-source.cnf \
  --single-transaction --quick --skip-add-drop-table --routines --events --triggers \
  "$source_database" > "$private_dump"
mariadb --defaults-extra-file=/run/secrets/game-db-target.cnf \
  --database="$target_database" < "$private_dump"
```

Compare o schema e as contagens de registros no estado final e valide as operações do jogo contra o destino. Antes do dump final, interrompa os escritores de todos os serviços que usam esse banco e preserve o dump e as verificações junto aos recibos do volume. Mantenha a cópia do volume intacta durante a verificação e a liberação. Se um arquivo precisar apontar para o novo banco, altere-o após liberar o destino, enquanto o container continua parado. Registre a alteração em armazenamento privado e teste a configuração antes da primeira inicialização. Se o rollback ocorrer depois de o destino aceitar novas gravações, sincronize o banco no sentido inverso junto com o volume. [Dump e restauração no MariaDB](https://mariadb.com/docs/server/clients-and-utilities/backup-restore-and-import-clients/mariadb-dump) · [Opções compatíveis do MySQL](https://dev.mysql.com/doc/refman/8.4/en/mysqldump.html)

## Importação de subusuários

Depois de importar contas e servidores, aplique as migrations `ShareServerMemberships` e `BindDelegationsToMemberRevision`. Exporte os vínculos da cópia do banco de origem e confira o plano antes de gravar.

```sh
(umask 077; set -C; php scripts/pterodactyl-migration/inventory.php --subusers > /caminho/privado/ptero-subusers.json)
export Migration__PterodactylSubusersFile='/caminho/privado/ptero-subusers.json'
dotnet run --project backend/ServerPanel.API -- --plan-pterodactyl-subusers
dotnet run --project backend/ServerPanel.API -- --import-pterodactyl-subusers
```

O importador vincula os UUIDs existentes e preserva direitos de console, energia, arquivos, agendamentos, backups, nome do servidor, assistente local e gestão de membros. O relatório `UnsupportedPermissions` identifica funções ainda indisponíveis, incluindo SFTP. O lote só grava quando todas as associações são válidas. Mudanças em uma associação já existente exigem revisão em **Membros**.

Na aba **Membros**, o proprietário ou administrador concede, edita ou revoga acesso usando o ID que a outra pessoa vê em **Minha conta**. O servidor aparece na lista da conta compartilhada. Alterar ou revogar as permissões invalida autorizações de download ainda não utilizadas. O worker pausa as agendas vinculadas à autorização anterior antes de executar a próxima tarefa. Um operador com os direitos necessários pode reativá-las.

As permissões `user.read`, `user.create`, `user.update` e `user.delete` delegam separadamente a listagem, inclusão, edição e revogação. Um membro só delega permissões que possui e só edita ou revoga vínculos cobertos por seu próprio conjunto de permissões. As próprias permissões, o proprietário e as contas administrativas ficam protegidos dessas ações. A inclusão vincula uma conta ArcadeNode existente pelo UUID.

## Importação de agendamentos

As agendas são vinculadas pelo UUID do servidor. Cadastre os servidores de destino com os mesmos UUIDs antes de importar. Confirme o fuso usado no painel de origem, `APP_TIMEZONE`. O ArcadeNode interpreta as expressões cron em UTC. Converta as expressões antes de ativar quando a origem usar outro fuso.

```sh
(umask 077; set -C; php scripts/pterodactyl-migration/inventory.php --schedules > /caminho/privado/ptero-schedules.json)
export Migration__PterodactylSchedulesFile='/caminho/privado/ptero-schedules.json'
dotnet run --project backend/ServerPanel.API -- --plan-pterodactyl-schedules
dotnet run --project backend/ServerPanel.API -- --import-pterodactyl-schedules
```

O manifesto contém os comandos das tarefas e deve ficar em armazenamento privado. A simulação aponta servidores ausentes e tarefas incompatíveis, incluindo regras de exclusão inválidas e atrasos acima de 60 segundos. Qualquer pendência impede a gravação do lote. O importador preserva a ordem, as exclusões de backup, os atrasos compatíveis e as condições das tarefas. As agendas são cadastradas pausadas para revisão e ativação após o corte.

Abra **Ver tarefas** para revisar a sequência, os atrasos e as exclusões antes de reativar a agenda. Use **Editar** para ajustar o cron e adicionar, remover ou reordenar tarefas enquanto a agenda está parada. O painel exige autorização para cada tarefa e pede a versão atual se alguém salvar ou reativar a agenda durante a edição. O comando `kill` aparece como **Forçar parada** e encerra o processo sem aguardar o salvamento do jogo. Quando uma tarefa de backup não tem uma lista explícita de exclusões, o node usa a `.pteroignore` que veio no volume. Use **Parar → Backup do volume → Iniciar** quando a agenda começar com o jogo online. [Backups locais e agendados](ARQUIVOS_NODES_PT-BR.md#backups-locais)

## Catálogo de eggs

No ArcadeNode, abra **Administração → Eggs e categorias**. Escolha ou crie a categoria e envie o arquivo JSON exportado pelo painel antigo. São aceitos os formatos `PTDL_v1` e `PTDL_v2` com até 64 KiB por manifesto. O reenvio do mesmo manifesto na mesma categoria retorna o registro existente. **Baixar JSON** recupera o documento original, incluindo os campos específicos da versão de origem.

No catálogo, copie o SHA-256 da egg revisada. No JSON privado carregado por `NodeConfigurationPath`, associe esse hash ao cartridge equivalente. Para a egg **Vanilla Minecraft** do Pterodactyl, use este perfil com o SHA-256 exibido pelo ArcadeNode.

```json
{
  "EggRuntimes": {
    "<SHA-256>": {
      "CartridgeId": "minecraft-java",
      "VariableMap": {
        "VANILLA_VERSION": "MC_VERSION",
        "SERVER_JARFILE": "SERVER_JAR"
      },
      "FixedValues": {
        "SERVER_TYPE": "vanilla"
      }
    }
  }
}
```

Depois de recarregar o painel, a ação **Criar servidor** abre o formulário com os valores e nomes da egg para as opções mapeadas. `VariableMap` liga cada nome de ambiente do Pterodactyl a uma variável editável do cartridge. O painel aplica as restrições de visibilidade e edição da egg, mantém `FixedValues` sob controle do perfil e valida os valores pelas regras do cartridge. Um perfil com apenas `CartridgeId` usa os padrões do cartridge. O servidor fica vinculado à egg de origem. Scripts de instalação, comandos de inicialização e URLs do manifesto ficam armazenados para consulta.

Reconstrua e aprove a imagem gerenciada do Minecraft nos nodes antes de criar servidores Vanilla com esse perfil. `latest` seleciona a versão estável publicada pela Mojang na primeira instalação. O instalador confere o checksum do JAR e grava a versão do Java usada para reinicializações. Para um volume já restaurado, indique `JAVA_RUNTIME=25` nas variáveis do manifesto quando o JAR exigir Java 25. [Manifesto da Mojang](https://piston-meta.mojang.com/mc/game/version_manifest_v2.json) · [Requisitos do Java para Minecraft 26.1](https://www.minecraft.net/en-us/article/minecraft-java-edition-26-1)

Na egg **Paper** do painel antigo, use o perfil abaixo sob o SHA-256 do próprio manifesto. No mapeamento de migração, inclua `MINECRAFT_VERSION`, `SERVER_JARFILE` e `BUILD_NUMBER` em `source_variables`.

```json
{
  "CartridgeId": "minecraft-java",
  "VariableMap": {
    "MINECRAFT_VERSION": "MC_VERSION",
    "SERVER_JARFILE": "SERVER_JAR",
    "BUILD_NUMBER": "PAPER_BUILD"
  },
  "FixedValues": { "SERVER_TYPE": "paper" }
}
```

Em uma instalação nova, o instalador da imagem usa o catálogo Fill do PaperMC para resolver a versão e a build estável, verifica o SHA-256 e grava a versão do Java. Para um volume migrado, o entrypoint usa o JAR e os arquivos de configuração preservados na raiz. [Serviço de downloads do PaperMC](https://docs.papermc.io/misc/downloads-service/) · [Versões do Java para Paper](https://docs.papermc.io/paper/getting-started/#requirements)

Para **Vanilla Bedrock**, aprove o cartridge `minecraft-bedrock` no SHA-256 da egg revisada. Mapeie `BEDROCK_VERSION` para `VERSION`, `SERVERNAME` para `SERVER_NAME`, `CHEATS` para `ALLOW_CHEATS`, `GAMEMODE` para `GAMEMODE` e `DIFFICULTY` para `DIFFICULTY`. Inclua em `source_variables` os nomes de origem que você mapear. Em uma restauração, essas opções permanecem no `server.properties` da origem. A imagem configura seu próprio caminho para bibliotecas. [Egg oficial do Pterodactyl](https://github.com/pterodactyl/game-eggs/blob/main/minecraft/bedrock/bedrock/egg-vanilla-bedrock.json)

Para **Arma Reforger**, use `arma-reforger`. O perfil pode mapear `SERVER_NAME`, `MAX_PLAYERS`, `SCENARIO_ID` e `VISIBLE` para os mesmos nomes. Mapeie `SERVER_PASS` para `SERVER_PASSWORD`, `BATTLEYE` para `BATTLYE` e `DISABLE_THIRD` para `DISABLE_THIRD_PERSON`. Em uma nova instalação, informe `ADMIN_PASSWORD` no ArcadeNode com pelo menos 16 caracteres sem espaços. Durante a restauração, a credencial existente fica no `config.json` copiado. Mantenha arquivos exportados com valores privados fora do repositório.

Aplique a migration `ImportPterodactylEggs` com a identidade de manutenção indicada no [guia de implantação](../deploy/README.md). A migration amplia a descrição e adiciona as colunas de manifesto e hash. O rollback da aplicação pode manter esse schema. O rollback do banco bloqueia a remoção das colunas enquanto houver manifestos armazenados.
