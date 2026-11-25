# Implantação endurecida

Este repositório prepara a aplicação e a configuração. Nenhum banco, daemon ou servidor real foi alterado durante as correções. Leia também [o relatório de correções](../docs/SECURITY_HARDENING_PT-BR.md).

## Banco e migração

1. Faça e teste um backup do banco e dos dados dos jogos. Pare as escritas da versão antiga durante a migração.
2. Use SQL Server em rede privada, com certificado válido para o hostname da conexão. Crie identidades separadas: manutenção com DDL somente no banco do painel e execução com DML nesse banco. Nenhuma delas deve ser `sa`. O [modelo de grants](grant-runtime.sql) pressupõe que o DBA já criou o login; não contém senha.
3. Coloque as conexões em arquivos de segredo fora do repositório. Exemplo de **formato**, com valores fictícios:

   ```text
   Server=tcp:sql.example.com,1433;Database=ArcadeNode;User ID=arcadenode_runtime;Password=<segredo>;Encrypt=True;TrustServerCertificate=False;Persist Security Info=False;Connect Timeout=10;
   ```

   A conexão de manutenção usa outro login. Use `SqlConnectionStringBuilder` ou a ferramenta do DBA para senhas com caracteres reservados. A API verifica TLS e rejeita identidades efetivamente administrativas ao servir requisições.
4. Prepare o segredo da senha inicial (8–72 bytes UTF-8, maiúscula, minúscula, número e símbolo) e uma chave JWT aleatória com pelo menos 48 bytes. A senha inicial só é usada se não existir SuperAdmin, pelo comando explícito `--migrate`. Para preservar os logins legados na migração, defina `ARCADENODE_BOOTSTRAP_USERNAME` e `ARCADENODE_BOOTSTRAP_EMAIL` no Compose com uma identidade livre no inventário de origem.
5. Copie `.env.example` para `.env`, preenchendo os caminhos e o hostname público. Garanta permissões de leitura apenas para o operador e o UID do respectivo processo no container: API/maintenance usam o `APP_UID` da imagem .NET; Nginx usa 101. Compose local monta secrets como arquivos; não é um cofre criptografado e não garante remapeamento de UID/modo dos arquivos da origem.
6. Revise [o SQL idempotente gerado](../docs/security/2026-11-16/security-migration.sql). A migração aborta se houver endpoints duplicados/com mais de 450 caracteres ou portas duplicadas por node. Corrija esses registros deliberadamente antes de prosseguir.
7. No ambiente de destino, valide a configuração e execute a manutenção explicitamente:

   ```bash
   docker compose config --quiet
   docker compose build backend frontend
   docker compose --profile maintenance run --rm migrate
   ```

   Como alternativa, o DBA pode executar o SQL revisado, mas deve provisionar o SuperAdmin separadamente; o script SQL não cria sua senha. A identidade que atende HTTP não aplica migrações.

O Compose compartilha o volume privado `data_protection_keys` entre a API e a manutenção. Proteja o volume com armazenamento cifrado, preserve as chaves ao recriar os containers e inclua a cópia correspondente no backup do banco. Em instalações com TOTP existente, restaure as chaves já usadas antes de subir a API. [Gerenciamento de chaves do ASP.NET Core](https://learn.microsoft.com/pt-br/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0)

**Efeitos esperados:** todos os JWTs antigos deixam de autenticar por não possuírem sessão no banco. Todos os convites anteriores são revogados, inclusive os não expirantes/ilimitados; gere novos convites após a atualização. O comando não remove jogos ou volumes.

## Nodes, imagens e quotas

- Remova a exposição de Docker sem autenticação em instalações antigas. O Compose novo não contém dind privilegiado, socket do host, SQL ou publicação da API. Atualizar este arquivo não desliga automaticamente um daemon antigo em outra máquina.
- Instale Docker Linux atualizado em hosts dedicados aos jogos. Separe a rede dos jogos da rede da API/banco e bloqueie acesso dos jogos ao plano de controle, metadados da nuvem e redes administrativas, inclusive em IPv6. Uma bridge com nome diferente não cria sozinha uma política de isolamento/egress.
- Habilite TLS com autenticação obrigatória de cliente no daemon. Libere 2376 somente para os endereços de saída do painel/VPN. O certificado do servidor precisa de SAN correspondente ao hostname/IP e EKU de servidor; o certificado do painel precisa de chave e EKU de cliente, assinado por uma CA privada dedicada. A implementação aceita TLS 1.2 ou superior e verifica hostname, validade, finalidade e CA. Não segue redirects nem proxies do sistema.
- O certificado de cliente continua sendo uma credencial administrativa do Docker no node: mTLS autentica o painel, mas não limita as operações que um processo comprometido poderia solicitar ao daemon. Mantenha esses hosts dedicados; uma evolução para agente com operações restritas/autorização no daemon reduz esse alcance. O painel não recebe mais o socket do host onde ele próprio roda.
- O exemplo usa folhas assinadas diretamente pela CA cadastrada; não baixa intermediários/AIA/CRL da rede. **Revogação on-line de certificados não foi implementada.** Use certificados de curta duração e rotação: remova a confiança na CA/cliente antigo no daemon, atualize os arquivos e reinicie a API (clientes/certificados são cacheados). Nunca distribua a chave privada da CA aos containers ou à API.
- Copie [nodes.example.json](nodes.example.json) para uma configuração administrada fora do repo. Cadastre origens HTTPS exatas; caminhos, userinfo, query, fragmento e origens desconhecidas são recusados. A API administrativa de teste não pode ampliar essa lista. O endereço `192.0.2.10` é documental e precisa ser substituído pelo IP real de publicação dos jogos.
- Configure a RAM e o disco disponíveis para os jogos no cadastro do node. Os limites efetivos incluem os percentuais de sobrealocação. No JSON privado, use `AllowedCpuCores` para listar os IDs lógicos elegíveis para seleção e `CpuCapacityPercent` para limitar a soma das quotas de CPU, contando `100` por CPU lógica. Escolha o teto a partir do hardware reservado para jogos. O campo é opcional para nodes que administram a sobrealocação de CPU externamente. Implantações e importações somam as reservas existentes antes de admitir outro servidor, incluindo destinos de migração já reservados.
- Crie uma rede dedicada no node e instale um driver de volume com quota. O driver incluído usa projetos XFS e confere a aplicação do limite no kernel. Teste a escrita até esgotar a quota antes de habilitar `QuotaEnforcementVerified`. Drivers externos precisam aplicar o limite configurado em bytes e reportá-lo em `Volume.Options`. [Instalação do driver de quota](quota/README.md)
- Construa as imagens novas de `game-servers/` e publique em registry controlado. Exemplo: `docker compose -f game-servers/docker-compose.yml --profile image-build build`. Precarregue no node os digests auditados; a API não baixa imagens arbitrárias. As definições standalone foram convertidas em build: `up` encerra sem iniciar jogos.
- Adicione cada digest à lista de imagens, com alias exato usado pelo cartridge/template e diretório home correspondente. Exemplo estrutural (substitua o digest pelo real):

   ```json
   { "Image": "registry.example.com/arcadenode/minecraft@sha256:<64-hex-reais>",
     "Alias": "arcadenode/minecraft:secure", "DataPath": "/home/minecraft" }
   ```

   | Cartridge | Alias | DataPath |
   | --- | --- | --- |
   | `minecraft-java` | `arcadenode/minecraft:secure` | `/home/minecraft` |
   | `minecraft-bedrock` | `arcadenode/minecraft-bedrock:secure` | `/home/bedrock` |
   | `project-zomboid` | `arcadenode/project-zomboid:secure` | `/home/pzserver` |
   | `arma-reforger` | `arcadenode/arma-reforger:secure` | `/home/reforger` |

   Produção exige digests SHA-256 reais, usuário 10001, label `arcadenode.managed=2` e ausência de `VOLUME` implícito. Toda a home fica em um único volume com quota. A raiz permanece somente leitura. Os entrypoints são imutáveis em `/usr/local/bin`. Senhas de administração e RCON devem ser únicas e ter pelo menos 16 caracteres.
- Esses aliases também selecionam a parada do jogo. Se houver um proxy adicional, configure o tempo de resposta de `POST /api/servers/{id}/power` para pelo menos 140 segundos, como no Nginx incluído. Isso permite aguardar o encerramento do processo e confirmar um reinício.
- Containers antigos precisam das labels de servidor, node e proprietário e da política atual de provisionamento. Faça backup e valide o conteúdo antes de restaurar em um container novo. Para servidores do Pterodactyl, siga o fluxo de importação e corte com verificação de volumes. [Guia de migração](../docs/MIGRACAO_PTERODACTYL_PT-BR.md)

## Entrada HTTPS e operação

Forneça certificado público/corporativo válido e chave privada nos arquivos TLS. Só Nginx publica 443, inicialmente em loopback. Configure `PANEL_BIND_IP` deliberadamente no host de destino após firewall/DNS/TLS. A API fica em HTTP apenas na rede privada entre os dois containers, aceita tráfego HTTPS encaminhado exclusivamente do proxy conhecido e não publica 5000.

O exemplo usa a sub-rede 172.30.0.0/24, proxy 172.30.0.10 e API 172.30.0.11. Se houver conflito de rede, ajuste **também** `Security__TrustedProxies__0`. Nginx sobrescreve os cabeçalhos forwarded recebidos. Um proxy/CDN adicional exige revisar a origem real do IP e a cadeia de confiança antes de alterar limites.

```bash
docker compose up -d backend frontend
```

Os instaladores remotos, resets de senha e limpezas antigos foram retirados. `scripts/deploy.sh`/`.ps1` apenas encaminham ações explícitas do Compose novo; `quick-deploy`, `init-setup`, os wizards e o wizard de deploy encerram com orientação.

A remoção de um servidor preserva seu volume de dados. Gerencie backup, retenção e exclusão desses volumes pelo operador, verificando o GUID/labels antes de apagar. Monitore espaço e defina retenção de auditoria e orçamento do banco. Rate limits locais não substituem controle de tráfego no proxy em uma implantação com várias réplicas.

## Aceite no ambiente de destino

Ainda é necessário executar e registrar, em homologação Linux com SQL Server e Docker reais:

- Migração em cópia de um banco existente, autenticação com grants de runtime e rejeição de credenciais administrativas/TLS inválido. Verifique também o lock transacional `sp_getapplock` entre dois processos de API durante power/suspensão; ele requer somente a associação padrão à role `public`, sem transformar runtime em db_owner.
- Build e análise de vulnerabilidades das imagens finais; renovação dos certificados; HTTPS real, headers e portas efetivamente alcançáveis.
- Inspeção dos limites efetivos de memória/CPU/pids/volume e tentativa de excedê-los; quota de disco aplicada pelo driver/filesystem; regras de egress e isolamento entre jogos.
- Instalação, início/parada e entrada do console de cada jogo sob UID 10001, raiz somente leitura e quota. Inclua SteamCMD, atualizações e o acesso às portas principal e secundárias reservadas na migração.

Em uma VM Alpine isolada, o cliente do painel autenticou com mTLS no Docker Engine 29.10.1, leu os contadores de CPU e o limite de memória de um container Linux e confirmou a entrega de uma linha do console pelos logs do container. Na mesma VM, o SQL Server 2022 em Docker aplicou a cadeia de migrations, preservou um registro legado e autenticou uma conta Pterodactyl sintética com a senha original e TOTP. O ensaio também verificou os locks em duas conexões com os grants de runtime. O node de arquivos rodou em Docker com UID não privilegiado e passou pelos testes de captura, restauração, integridade e permissões de volumes sintéticos. A imagem atual da API também gravou e releu o volume privado de chaves em dois containers sucessivos executados sem root. O Nginx executado sem root serviu o frontend por HTTPS na VM e enviou as políticas CSP e HSTS. O aceite no destino exige as imagens dos jogos, a rede, o driver de quota e o banco de homologação.

No ensaio conjunto de quota e arquivos, o XFS limitou a escrita de um container a 32 MiB, preservou o volume com dados e manteve a leitura após reiniciar o driver. Um node de arquivos já em execução descobriu o volume pelo UUID via configuração gerenciada. As chamadas mTLS confirmaram download, upload, medição e backup. O mesmo UUID passou a apontar para o conteúdo novo após a recriação do volume. [Configuração dos nodes de arquivos](../docs/ARQUIVOS_NODES_PT-BR.md)
