# Security regressions

Requer SDK .NET 10. Executa a API real em `TestServer`, filtros MVC, autenticação, serviços EF e cliente Docker reais. SQLite em diretório temporário substitui SQL Server. Dois endpoints TLS de loopback simulam o protocolo Docker, incluindo attach de stdin; nenhum comando, jogo ou container é executado. Certificados e contas são sintéticos.

```powershell
dotnet run --project scripts/security-probes/SecurityProbes.csproj --no-launch-profile
```

A saída JSON traz `mode: secure-regression` e a contagem em `passed`. O código de saída 0 confirma o sucesso. O Linux também executa regressões nativas de arquivos. O processo aguarda as requisições e auditorias terminarem e encerra API, sockets, certificados e banco temporário.

Em Linux, a suíte executa um grupo adicional para verificar leitura de arquivo regular, bloqueio de caminhos absolutos, travessia e links simbólicos no leitor do node. Esse grupo requer kernel com suporte a `openat2` e acesso a `/proc/self/fd`.

O workflow Linux também sobe a imagem do node em Docker com certificados temporários e um volume de teste. O teste verifica certificado obrigatório, isolamento por UUID, rejeição de links simbólicos, download, upload, criação e movimentação de arquivos e pastas sem sobrescrita, edição com SHA-256, medição de disco e recuperação de um backup com conteúdo, datas e permissões preservados.

O workflow verifica o instalador Vanilla com um manifesto sintético e testa Java 21 e 25 na imagem gerenciada do Minecraft.

O teste em Docker repete a exclusão de um backup e verifica que outros arquivos e links permanecem intactos. A suíte da API verifica a proteção, a reserva por agendamento ou restauração e a repetição segura quando um node volta a responder.

A cópia de arquivos tem cobertura para isolamento por servidor, estado do container, preservação de bytes e permissões, limite de tamanho e conflito de destino.

A compactação de uma seleção testa a permissão `file.archive` separada da criação de arquivos. O teste Linux verifica os caminhos e os bytes do TAR, a recusa de links, o limite de dados e a preservação de um arquivo de destino existente.

A extração cobre ZIP e TAR nos testes gerenciados, incluindo integridade dos bytes, caminhos, metadados e limites. O teste Linux cobre a publicação da pasta, a preservação do arquivo compactado e a limpeza de uma extração rejeitada.

O teste de upload pausa a origem após o primeiro bloco e confirma que o node já recebeu esse bloco por mTLS. As verificações Linux cobrem um envio acima de 1 MiB, a recusa da sobrescrita e o descarte de um envio interrompido.

A recuperação de backups do Pterodactyl testa o tamanho e os checksums registrados, cabeçalhos TAR sem diretórios explícitos e preservação do arquivo original em caso de rejeição.

A migração de subusuários testa a repetição do lote, o isolamento de permissões por ação e a revogação de agendas e autorizações de download.

A gestão delegada verifica os limites dos direitos repassados, os vínculos protegidos, a inclusão concorrente e a separação das quatro permissões de membros.

O mesmo teste cria um container sintético com os rótulos do Wings e verifica que a captura aceita somente a origem parada. Um reinício da origem invalida o recibo anterior. A suíte da API testa a liberação administrativa com relatórios consistentes e o destino ainda parado.

Os ensaios com servidores oficiais rodam no GitHub Actions ao enviar um branch `ci/official-games/bedrock/*` ou `ci/official-games/zomboid/*`. Cada prefixo aciona apenas o jogo correspondente e a suíte de regressões. Quando o workflow estiver no branch padrão, **Security regressions → Run workflow** também oferece as opções `official_bedrock` e `official_zomboid`. [Eventos do GitHub Actions](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_dispatch)

O runner baixa a versão estável da Mojang, inicia a origem com os rótulos do Wings e faz uma cópia inicial enquanto o jogo roda. O teste grava uma pontuação de 42 no mundo, para a origem pelo console, sincroniza os últimos dados e verifica o recibo v3 como UID 10001. O container restaurado precisa ler a mesma pontuação. As duas instâncias de jogo rodam sem rede e usam volumes descartáveis. [Comandos do placar do Bedrock](https://learn.microsoft.com/en-us/minecraft/creator/documents/scoreboardintroduction?view=minecraft-bedrock-stable) · [Console de containers Docker](https://docs.docker.com/reference/cli/docker/container/attach/)

O ensaio do Project Zomboid baixa o servidor oficial pelo SteamCMD no runner. Usa `legacy41` por padrão, inicia um mundo no layout do Pterodactyl e adiciona um jogador depois da primeira cópia. O destino precisa iniciar com a mesma árvore de arquivos, passar na verificação do SQLite e preservar as credenciais do banco do jogo. As duas instâncias rodam com `-nosteam` e sem rede. O runner usa XFS com reflink para compartilhar os blocos dos arquivos idênticos, mantém os diretórios independentes e limpa o volume ao sair. Em um runner Linux descartável, `ZOMBOID_SMOKE_BRANCH` permite testar outra branch do Steam, inclusive `public`.

Em um host Linux de homologação, rode os mesmos passos com Docker local, SDK .NET 10, Python 3, `jq`, `rsync` e `sudo` sem interação para as operações nos volumes de teste.

```sh
docker build -t arcadenode-bedrock-check ./game-servers/minecraft-bedrock
bash scripts/security-probes/bedrock-cutover-smoke.sh arcadenode-bedrock-check
```

```sh
docker build -t arcadenode-files-check ./backend/ArcadeNode.Node
bash scripts/security-probes/node-container-smoke.sh arcadenode-files-check
```

O teste de SQL Server usa um container descartável e aplica a cadeia de migrations sobre o schema antigo com um registro sintético. Em seguida aplica os grants de implantação a uma identidade de runtime, verifica os privilégios dessa conta, autentica um usuário Pterodactyl importado com o hash PHP original e TOTP após abrir o mesmo diretório de chaves em outro provider e testa a exclusão mútua dos locks de servidor e de membros em duas conexões de runtime. [SQL Server em Docker](https://learn.microsoft.com/sql/linux/install-upgrade/quickstart-install-docker)

```sh
bash scripts/security-probes/sqlserver-container-smoke.sh
```

A suíte também pode verificar a importação e exportação de eggs de uma instalação antiga. Passe os caminhos dos JSONs após `--`. Os manifestos são carregados apenas na base temporária do teste.

Os dois cenários adicionados de diagnóstico verificam referência HTTP/log, preservação do código de falha Docker e ausência de credenciais/exceções cruas nos avisos. A saída inclui algumas amostras sanitizadas em `diagnosticSamples`. Consulte [o mapa de avisos](../../docs/DIAGNOSTICS_PT-BR.md).

Os cenários cobrem autorização A/B, campos administrativos, limites e capacidade, portas, origem Docker cadastrada, mTLS positivo/negativo, isolamento por node/labels, imagens antigas, quotas configuradas, stdin sem exec, logs/paginação/allocations, concorrência, expiração/revogação de convites e sessões e rate limits com cabeçalhos forjados.

O harness anterior reproduzia vulnerabilidades e foi substituído por estas regressões. Seus JSON históricos foram preservados em `docs/security/2026-11-16/`; não representam o comportamento esperado atual. Veja [o relatório das correções](../../docs/SECURITY_HARDENING_PT-BR.md).

Limitações: SQLite não substitui a migração e testes de concorrência no SQL Server. O simulador confirma transporte e payloads, sem validar cgroups, quota física, kernel, imagens ou compatibilidade dos jogos. O aceite real está em [deploy/README.md](../../deploy/README.md).
