# Revisão de segurança — ArcadeNode

> Registro histórico anterior às correções. Consulte [as correções e validações posteriores](SECURITY_HARDENING_PT-BR.md) para o estado atual. Os achados e números de linha abaixo foram preservados como evidência da revisão original.

Data: 16/11/2026. Base: `main`, commit `9bd4f9a`, incluindo as alterações locais de frontend já existentes. Revisão de código/configuração, dependências e provas locais isoladas. Nenhuma correção de produção aplicada nesta revisão.

## Veredito

**Risco alto, com configuração crítica de infraestrutura. Não considero este protótipo pronto para exposição pública ou hospedagem de clientes.**

Os problemas principais são limites de confiança e de recursos, autorização e configuração operacional. Não há evidência de que a escolha de C# seja a causa. Trocar de linguagem sem corrigir esses fluxos manteria os mesmos riscos.

Foram agrupados **11 achados: 1 crítico, 5 altos e 5 médios**. As severidades são qualitativas, considerando um painel que hospeda jogos de usuários distintos; não são pontuações CVSS nem uma medição do servidor em produção. Avisos de dependências e observações complementares não são somados como novas falhas independentes.

## Escopo e limites

- Revisados API C#, controllers, DTOs, serviços, autenticação, banco, Docker/Compose, middleware, scripts, imagens de jogos e autenticação/preview do frontend.
- Oito cenários reproduzidos por um executável de diagnóstico que referencia o código real da API. Controllers usam serviços simulados; o cliente Docker real fala somente com um servidor falso efêmero em `127.0.0.1`.
- Nenhum comando de shell foi executado pelos probes; nenhum container foi criado; nenhum banco ou node real foi acessado. Não houve teste de invasão de infraestrutura externa, escape de container ou varredura de portas.
- O harness roda em .NET 10, disponível nesta máquina, referenciando o projeto net9.0. O teste adicional de antiforgery usa o framework instalado do harness. Isso não substitui um teste HTTP completo da API net9.0 com SQL Server/Docker.
- Firewall, TLS e imagens efetivamente implantadas não foram inspecionados. Publicações de portas no Compose são evidência de configuração, não prova de alcance pela internet.
- Os relatórios antigos em `AUDIT_REPORT.md`/`BACKEND_AUDIT.md` não foram usados como prova de problemas atuais.

## Achados principais

| ID | Severidade | Achado | Evidência principal |
| --- | --- | --- | --- |
| F01 | Crítico | Docker publicado sem TLS/autenticação, node privilegiado e socket do host montado na API | `docker-compose.yml:43`, `:65`, `:67`, `:74` |
| F02 | Alto | Console de usuário executa comandos do sistema operacional | `Services/DockerService.cs:359`, `:387`; probe P04 |
| F03 | Alto | Estouro de inteiro remove limites de memória; isolamento de recursos incompleto | `Services/DockerService.cs:74`; probes P03/P05 |
| F04 | Alto | JWT não é revogado após troca de senha ou mudança do estado da conta | `Services/AuthService.cs:166`, `Program.cs:91` |
| F05 | Alto | Deploy padrão em Development/HTTP e aplicação conectada como `sa` | `docker-compose.yml:11`, `:30`, `:32`, `:36` |
| F06 | Alto | Plataforma .NET 9 fora de suporte; dependências com avisos pendentes | `ServerPanel.API.csproj:4`, imagens `9.0`, auditorias de pacotes |
| F07 | Médio | Proprietário altera campos administrativos e valores inválidos | `Controllers/ServersController.cs:144`, `DTOs/DTOs.cs:55`; P02 |
| F08 | Médio | Status de deployment de outro usuário sem verificação de propriedade | `Controllers/DeploymentController.cs:212`; P01 |
| F09 | Médio | Teste de node permite alcançar destinos arbitrários a partir da API | `Services/NodeService.cs:204`; P06 |
| F10 | Médio | Uso de convites sem proteção de concorrência e geração não criptográfica | `Services/AuthService.cs:82`, `:118`; `Controllers/AdminController.cs:319` |
| F11 | Médio | Limites de disponibilidade inadequados: login compartilhado, listas/logs/allocations sem limites adequados | `Program.cs:111`, `Common/Pagination.cs:26`, `Services/DockerService.cs:296`, `Services/NodeService.cs:156` |

Os caminhos da tabela que começam em `Services`, `Controllers`, `DTOs` ou `Common` são relativos a `backend/ServerPanel.API/`.

### F01 — Plano de controle do Docker exposto

**Evidência:** o Compose publica `2375:2375`, limpa `DOCKER_TLS_CERTDIR` e define `privileged: true` no Docker-in-Docker. A API também recebe `/var/run/docker.sock` do host. `DaemonToken` é armazenado, mas não é usado para autenticar conexões do DockerClientFactory nem do teste de conexão.

**Condição de exploração:** um atacante conseguir alcançar a porta do daemon quando essa configuração estiver em execução. Ele não precisaria autenticar no painel para conversar diretamente com o Docker sem autenticação.

**Impacto:** controle dos containers, volumes e operações do daemon acessível. Como se trata de um node Docker-in-Docker privilegiado, o risco para o host exige avaliação adicional; não foi demonstrado escape para o host. A montagem do socket real na API amplia o impacto de um eventual comprometimento desse processo.

**Correção:** remover a publicação pública de 2375 e o socket desnecessário; separar painel e execução; usar transporte autenticado, como mTLS ou um agente autenticado com operações limitadas; restringir origens na rede. Um token salvo no banco não protege a API do Docker por si só. [Documentação oficial do Docker sobre proteção do daemon](https://docs.docker.com/engine/security/protect-access/).

### F02 — Console do jogo equivale a shell no container

**Evidência:** a rota `/api/servers/{id}/command` admite o proprietário de um servidor em execução. O serviço encaminha `Cmd = ["/bin/sh", "-c", command]`. Bloquear `;`, `$`, aspas e outros caracteres não impede programas simples do sistema operacional.

**Reprodução P04:** a entrada inofensiva `id` chegou ao endpoint Docker falso como `[/bin/sh, -c, id]`. O probe capturou a requisição; não executou o comando.

**Impacto:** usuário com acesso ao console obtém capacidade de executar programas com as permissões do usuário do container, em vez de somente comandos do jogo. Pode modificar/apagar arquivos acessíveis, consumir recursos ou acessar destinos de rede disponíveis. Não foi comprovada execução no host. A imagem Zomboid usa permissões 777 e não define um `USER` próprio; o usuário efetivo depende da imagem base.

**Correção:** enviar comandos pelo stdin do processo do jogo ou por RCON com autenticação, respeitando capacidades por usuário. Remover o caminho genérico de shell e aplicar usuário não privilegiado, limites e isolamento de rede.

### F03 — Limite de memória incorreto e quotas inconsistentes

**Evidência:** `server.MemoryLimit * 1024 * 1024` faz a multiplicação em `int` antes de atribuir ao campo longo do Docker. A criação por cartridge não contém as verificações de capacidade do node presentes no fluxo antigo; seus DTOs não têm `Range` para memória/CPU/disco/porta. `DiskLimit` não aparece como quota no HostConfig.

**Reprodução P05, capturando o JSON enviado pelo cliente Docker real:**

| Memória solicitada | Valor de memória enviado ao Docker |
| --- | --- |
| 1024 MB | 1073741824 bytes |
| 2048 MB | -2147483648 bytes — inválido |
| 4096 MB | 0/omitido — sem limite explícito |
| 8192 MB | 0/omitido — sem limite explícito |

P03 confirmou também que o DTO de cartridge aceita memória/CPU/disco zero e porta -1. O resultado do Docker real ao receber valores inválidos não foi exercitado. Sem limites, um container pode consumir os recursos disponíveis e afetar outros jogos. [Restrições de recursos no Docker](https://docs.docker.com/engine/containers/resource_constraints/).

**Correção:** multiplicação verificada em 64 bits (`checked((long)memoryMB * 1024 * 1024)`), limites consistentes em todos os DTOs, reserva atômica de capacidade e implementação de quotas de disco/processos. Conferir também a configuração de CPU adequada aos nodes Linux e o comportamento sob carga.

### F04 — Tokens roubados continuam válidos após troca de senha

**Evidência:** o JWT é validado por assinatura, emissor, audiência e tempo de vida. Não há consulta do estado do usuário em `OnTokenValidated`, versão de sessão/security stamp ou lista de revogação. `ChangePasswordAsync` troca apenas o hash. O logout do frontend limpa o estado local.

**Impacto:** um token já obtido continua aceito até expirar, mesmo após trocar a senha. Alterações/remoção da conta diretamente no banco também não invalidam automaticamente as claims já assinadas. No padrão configurado, a validade é 60 minutos, sujeita à tolerância de relógio do validador. Não foi simulado roubo de token nem chamada à API real.

**Correção:** versão de sessão ou security stamp validada no servidor; revogar sessões após mudança de senha/estado/papel; logout com efeito no servidor; definir a estratégia de expiração/renovação. Assinatura JWT correta não resolve revogação.

### F05 — Configuração de desenvolvimento exposta e privilégio excessivo no banco

**Evidência:** Compose fixa `ASPNETCORE_ENVIRONMENT=Development`, publica a API em 5000 e SQL Server em 1433 sem restrição explícita de interface. O frontend Nginx serve HTTP. A conexão da aplicação usa o usuário `sa`, com `TrustServerCertificate=True`. O ramo que aplica HTTPS/HSTS no backend fica desativado em Development.

**Impacto:** na configuração padrão, credenciais/tokens do painel trafegam por HTTP se não houver TLS externo. Swagger e mensagens de desenvolvimento aumentam a exposição de informações. Um comprometimento da aplicação permite operar o banco com privilégios muito acima dos necessários. Publicar 1433 não é, isoladamente, bypass de autenticação do SQL Server.

**Correção:** configuração de produção explícita, HTTPS no ponto de entrada, API/banco em rede privada, usuário SQL de menor privilégio e identidade separada para migrations. Validar proxy/forwarded headers e TLS real; cabeçalho HSTS em resposta HTTP não cifra uma conexão.

### F06 — .NET 9 fora de suporte e dependências com avisos

O projeto aponta para net9.0, pacotes Microsoft 9.0.0 e imagens SDK/runtime 9.0. Em **16/11/2026**, .NET 9 já está fora de suporte: o encerramento foi **10/11/2026**, conforme a [política oficial da Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). Migrar para uma linha suportada, como .NET 10 LTS, com patches atuais.

O `npm audit` encontrou **12 pacotes sinalizados: 4 high e 8 moderate**, incluindo dependentes/transitivos. Isso não equivale a 12 vulnerabilidades independentes exploráveis pelo visitante. Há avisos de Axios 1.18.0, Vite 8.3.1, follow-redirects, source-map-js e ferramentas de desenvolvimento.

O Axios deste projeto roda no navegador; vários avisos referem-se somente ao adaptador Node. Por exemplo, o [aviso oficial GHSA-c29m-xwm3-cm6r](https://github.com/axios/axios/security/advisories/GHSA-c29m-xwm3-cm6r) exclui o adaptador fetch/XHR do navegador. Vite é servidor de desenvolvimento, não o Nginx que hospeda o bundle final. Atualizar de forma controlada, mas não rotular esses avisos automaticamente como RCE da API C#.

`dotnet list ... package --vulnerable --include-transitive --format json` não retornou advisories de pacotes NuGet resolvidos. Esse resultado não cobre CVEs do runtime/imagens nem elimina o problema de fim de suporte. Não houve análise de imagens Docker/SBOM, pois as imagens não estão instaladas.

### F07 — Autorização por campo ausente na edição de servidor

**Evidência:** `UpdateServer` verifica apenas acesso ao servidor e repassa o DTO inteiro ao serviço, que persiste startup, variáveis de ambiente e limites de recursos. O DTO de atualização não valida intervalos.

**Reprodução P02:** um proprietário com papel `User` conseguiu encaminhar memória/CPU/disco -1 e startup pelo controller; a validação de atributos aceitou o DTO. Persistência foi verificada por leitura do serviço, sem SQL Server no teste.

**Impacto:** alteração de dados de administração e contabilidade de quotas por usuário comum, inclusive valores negativos que podem adulterar os totais do node. Importante: o serviço atual não redimensiona nem recria o container na atualização; não foi demonstrado aumento imediato de RAM ou execução do startup alterado.

**Correção:** separar edição de identificação de edição administrativa; aplicar autorização por operação/campo e validação centralizada; recalcular/reservar recursos em transação.

### F08 — IDOR de status de deployment

**Evidência:** `/api/deployment/status/{serverId}` exige autenticação, mas não testa proprietário ou papel. Outros endpoints de servidor têm `CanAccessServer`.

**Reprodução P01:** com identidade de usuário alheio, o status retornou `OkObjectResult`, enquanto o detalhe do mesmo servidor retornou `ForbidResult`.

**Impacto:** divulgação de existência, estado e progresso de um servidor alheio para quem conhece seu GUID. Esse endpoint não retorna arquivos, credenciais ou controle de energia; o GUID não foi demonstrado como enumerável.

**Correção:** política de acesso ao recurso compartilhada por todos os controllers e testes HTTP com usuário A, B e administrador.

### F09 — Destinos de rede sem restrição no teste de node

**Evidência:** a API aceita `DockerEndpoint` e o usa diretamente no DockerClientConfiguration. Não há restrição de esquema, host, porta ou destinos internos. A rota é restrita a `Admin`/`SuperAdmin`.

**Reprodução P06:** o serviço atingiu o endpoint falso em loopback e consultou `/info`, `/version` e containers. Nenhum destino de rede real foi sondado.

**Impacto:** um administrador comprometido ou menos confiável pode usar a rede/identidade do painel para alcançar daemons/endpoints não previstos. Trata-se de capacidade SSRF limitada às operações do cliente Docker; não foi comprovado acesso arbitrário a metadados cloud ou leitura irrestrita de respostas HTTP.

**Correção:** enrollment explícito de nodes, esquemas/portas permitidos, restrição de saída de rede e autenticação do destino; validação de resolução DNS conforme a topologia. Como nodes legítimos podem ser privados, bloquear toda rede privada indiscriminadamente não é solução suficiente.

### F10 — Convites sujeitos a corrida

**Evidência:** registro lê `invite.IsValid`, cria usuário, incrementa `TimesUsed` em memória e salva. Não há concurrency token, atualização condicional do contador ou transação com isolamento de consumo. `new Random()` gera os códigos de acesso.

**Impacto:** duas requisições concorrentes com usuários diferentes podem validar o último uso simultaneamente e consumir o mesmo convite além do limite. A unicidade do código impede códigos duplicados, não o consumo simultâneo. Não houve reprodução concorrente em SQL Server; é um achado de revisão de lógica, a validar em integração. Não foi demonstrado ataque prático de previsão de `Random`.

**Correção:** consumir o uso atomicamente e criar a conta na mesma transação; gerar convites com RandomNumberGenerator, expiração e unicidade garantidas.

### F11 — Proteções de disponibilidade insuficientes

**Evidência:** login/cadastro compartilham a política fixa `auth`, com 10 requisições/minuto por instância, sem particionamento por cliente/conta. As listagens retornam tudo quando `pageSize` é omitido. Logs aceitam `tail` sem limite de aplicação e são acumulados em MemoryStream. Criação de allocations percorre um intervalo fornecido pelo cliente sem limites de faixa/lote; há consultas ao banco dentro do laço.

**Impacto:** tentativas anônimas podem consumir a janela de autenticação de outros usuários. Usuários autenticados podem exigir respostas/trabalho excessivos dentro de suas permissões; allocations é administrativo. Não houve teste de estresse ou esgotamento de memória, nem demonstração de DDoS. O timeout de logs existente não é um limite de bytes.

**Correção:** limitar por IP/conta e também globalmente; paginar com máximo obrigatório; limitar linhas/bytes de logs, número de allocations e operações simultâneas; usar cancelamento e retenção de logs. [Rate limiting no ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-9.0).

## Observações complementares, sem inflar a contagem

- **Interpolação de startup:** P07 confirmou que uma senha sintética com substituição de shell passa pela validação do cartridge e sobrevive à construção do startup. Porém somente admins criam servidores, já existe criação com startup livre, e o entrypoint Zomboid atual não executa o CMD recebido. Portanto não classifico isso como nova escalada/RCE comprovada. Evitar comandos de shell construídos por texto antes de integrar o executor de cartridges.
- **CSRF no pós-login:** P08 confirmou rejeição de token antiforgery emitido para identidade anônima quando reutilizado após autenticação. Um GET autenticado pode renovar o token. É um defeito de fluxo que pode gerar o primeiro POST com 403; não é prova de bypass CSRF. O middleware usa a validação real do ASP.NET, apesar do comentário simplificar como double-submit.
- **Segredos e defaults:** imagens/scripts ainda contêm `changeme` como fallback de senha de jogos. O bootstrap imprime uma senha administrativa gerada se não foi configurada; wizards imprimem senhas geradas e scripts usam arquivos temporários de reset. Restringir acesso a logs/temporários e remover defaults inseguros. A busca limitada de padrões de chaves/tokens não encontrou segredos reais, mas não cobre todo histórico Git nem todos os tipos de credencial.
- **Token no navegador:** JWT permanece no sessionStorage e é acessível a JavaScript. Isso aumenta o impacto de XSS, mas não foi identificado um sink XSS explorável. Não classifico sessionStorage sozinho como falha crítica.
- **Auditoria:** há middleware de auditoria, sem bodies com credenciais, mas login/cadastro podem aparecer sem identidade por ocorrerem antes da autenticação do token emitido. A trilha precisa registrar o sujeito autenticado e resultados de negócio. Logs são gravados no mesmo contexto de dados, sem retenção definida.
- **Múltiplos nodes:** DockerService usa sempre `GetClient()` padrão. A separação por NodeId é metadado, não isolamento de execução real. Corrigir isso antes de prometer isolamento por máquina; a factory aceita endpoints, mas as operações atuais não selecionam o node.
- **Imagens e downloads:** tags mutáveis e downloads de binários sem verificação completa de integridade exigem uma etapa de cadeia de suprimentos. Não foram calculadas ou inventadas CVEs de imagens ausentes.

## Controles positivos encontrados

- A aplicação recusa inicialização sem chave JWT configurada ou com chave curta; não há fallback JWT estático no código atual.
- BCrypt, política de senha, bloqueio temporário após falhas e rate limiting de autenticação existem.
- Cadastro exige convite e fixa o papel `User`; não há atribuição de papel pelo request de cadastro.
- Rotas de nodes e criação/exclusão de servidores têm restrições administrativas; os detalhes/logs/energia de servidor verificam propriedade.
- Uso de LINQ/EF Core no acesso revisado; não foram encontrados sinks de SQL bruto no backend pesquisado.
- Console do frontend renderiza texto por React; não foram encontrados `dangerouslySetInnerHTML`/`innerHTML` no código pesquisado.
- CSP e outros headers estão presentes no Nginx. A prévia está limitada a desenvolvimento, sem requests reais e sem bypass no backend; o build de produção não inclui seus fixtures.

Esses controles corrigem parte dos problemas descritos nos relatórios antigos, mas não compensam F01–F06.

## Ordem de correção e critérios de aceite

1. **Conter infraestrutura:** Docker e SQL privados, sem socket do host na API se dispensável, transporte autenticado para nodes e TLS obrigatório no painel. Confirmar com teste de rede a partir de uma máquina externa autorizada.
2. **Fechar execução e quotas:** remover shell do console, corrigir aritmética, centralizar validação e aplicar limites no runtime. Testar 1/2/4/8 GB, saturação controlada e persistência por node em ambiente descartável.
3. **Corrigir identidade/permissões:** revogação de sessões, autorização por campo e propriedade em status. Testar usuário A/B/Admin e token anterior à troca de senha.
4. **Atualizar a plataforma:** .NET suportado e pacotes/imagens corrigidos; repetir auditorias e build. Aplicar atualizações em mudança separada, sem `audit fix --force` indiscriminado.
5. **Resiliência:** consumo atômico de convites, limites de lotes/logs/paginação, auditoria e autenticação sem uma janela única compartilhada por todos.

Só depois executar um teste de integração completo, seguido de revisão de configuração do ambiente onde os nodes serão hospedados. A migração do Pterodactyl deve partir dessa base corrigida.

## Evidências e reprodução

- [Saída dos oito probes](security/2026-11-16/probes.json)
- [Auditoria npm integral](security/2026-11-16/npm-audit.json)
- [Auditoria NuGet integral](security/2026-11-16/nuget-audit.json)
- [Busca limitada de padrões de segredos](security/2026-11-16/secret-scan.json)
- [Código do harness](../scripts/security-probes/Program.cs)

Na raiz, com SDK .NET 10: `dotnet run --project scripts/security-probes/SecurityProbes.csproj --no-launch-profile`. Saída `reproduced: true` significa que a condição problemática continua reproduzível, não que o sistema é seguro. Os probes são deliberadamente diagnósticos; devem ser transformados em regressões de comportamento seguro durante as correções.
