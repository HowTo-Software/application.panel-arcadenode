# Mapa de avisos e pontos de falha

Os prints usam códigos permanentes, mensagens em português/inglês e logs estruturados. Procure pelo código com `rg`, por exemplo `rg "DockerVolume|AN0502" backend frontend/src`. O catálogo da API está em [DiagnosticPoints.cs](../backend/ServerPanel.API/Diagnostics/DiagnosticPoints.cs); o navegador usa [diagnostics.ts](../frontend/src/lib/diagnostics.ts).

Exemplo de uma falha registrada pela API:

```text
[AN0502] Caiu aqui: criação ou validação do volume com quota / Failed here: quota volume creation or validation.
TraceId=0HN... Source=DockerService.cs:39 Member=CreateContainerAsyncCore ServerId=<guid> NodeId=<guid> RootErrorType=DockerApiException DockerStatus=InternalServerError
```

`Source`/`Line` e `Member` identificam o ponto que registrou o aviso. `ServerId`/`NodeId` são referências internas. O tipo do erro, número SQL, status Docker e erro de socket aparecem quando disponíveis; mensagens cruas de exceção, stack traces, senhas, JWTs, convites, variáveis de ambiente, comandos, corpos e cabeçalhos de autenticação não são enviados pelos avisos novos ao logger.

## Onde consultar

- Backend: terminal do `dotnet run` ou `docker compose logs --tail 200 backend`. Para acompanhar somente os marcadores no Bash: `docker compose logs -f backend | rg 'AN[0-9]{4}'`.
- PowerShell: `docker compose logs -f backend | Select-String 'AN[0-9]{4}'`.
- Frontend: ferramentas do navegador → Console, pesquisando `ANUI`. Em Network, abra a resposta que falhou e veja `X-Diagnostic-Code` e `X-Trace-Id`.
- A API inclui os dois cabeçalhos nas respostas de erro; exceções tratadas também levam `diagnosticCode` e `traceId` no JSON. Erros normais de validação/login/autorização podem ter somente os cabeçalhos. Quando disponíveis, ambos aparecem na referência da mensagem de erro do frontend.
- Pesquise o mesmo `TraceId` no backend para seguir a requisição entre os serviços. Uma falha pode gerar registros na etapa e no encerramento HTTP; o código da etapa original é preservado ao propagar a exceção.
- O teste administrativo de node retorna HTTP 200 com `success: false` em falha de conexão; nesse caso a mensagem contém `AN0401` e o aviso detalhado fica no backend.

Avisos/erros ficam ativos por padrão. Inícios e conclusões de etapas ficam em `Debug`, para evitar prints a cada consulta bem-sucedida. Ative temporariamente `Logging__LogLevel__ServerPanel.API=Debug` no ambiente de execução, ou configure `Logging:LogLevel:ServerPanel.API` em configuração local. Não é necessário habilitar logging sensível do EF ou imprimir configurações. No PowerShell: `[Environment]::SetEnvironmentVariable('Logging__LogLevel__ServerPanel.API', 'Debug', 'Process')` antes de iniciar o processo.

Uma requisição bem-sucedida com mais de três segundos produz `AN0807`. O limiar é configurável por `Diagnostics__SlowRequestMilliseconds`, limitado entre 100 e 120000 ms. Provisionamentos podem demorar legitimamente: o aviso indica o que investigar, não prova uma queda. Falhas normais do frontend e operações canceladas pelo Axios são diferenciadas; cancelamentos esperados não geram aviso de rede.

## Como investigar

1. Copie o código e o `TraceId`, sem copiar credenciais ou o corpo da requisição.
2. Localize o código na tabela abaixo e o arquivo/método em `Source`.
3. Confira a dependência indicada: configuração, banco, certificado, node, driver de volume, imagem ou jogo.
4. Repita a operação após corrigir a causa e acompanhe os registros da mesma etapa. Com `Debug`, o último marcador de início sem conclusão ajuda a localizar uma operação travada.

`AN0002` confirma que o host da API iniciou; não certifica que todos os nodes, jogos ou cartridges estão saudáveis. Se o processo nem chegar à execução do código .NET (runtime ausente, container encerrado/OOM), consulte o supervisor/Docker/host. O código não consegue imprimir depois que o processo é morto.

O ArcadeNode consulta os containers dos nodes cadastrados a cada 15 segundos. Uma diferença na listagem passa por nova inspeção autenticada antes de alterar o estado registrado. Uma falha de comunicação preserva o último estado confirmado e os servidores suspensos ficam fora desse fluxo. Na página do servidor, um encerramento com erro mostra o código de saída ou a indicação de falta de memória informada pelo Docker. Agendas restritas a servidores online confirmam o estado do container antes de começar.

Se não houver referência da API e o navegador mostrar falha de rede, confira também o Nginx: `docker compose logs --tail 100 frontend`. Certificado TLS, resolução do upstream `backend`, porta 443 e a rede estão em `frontend/nginx.conf`/`docker-compose.yml`. Esses erros anteriores à API aparecem nos logs nativos do proxy. Se o bundle JavaScript nem carregar, use Console/Network nativos do navegador: os listeners da aplicação ainda não estarão ativos.

## Navegador

| Código | Português / English | Verificar |
| --- | --- | --- |
| ANUI001 | Caiu aqui: requisição à API / Failed here: API request | Status, código da API e referência retornados; permissões e validação. |
| ANUI002 | API sem resposta ou tempo esgotado / API unavailable or timed out | API, proxy, conexão e timeout; consulte Network. O navegador não revela a causa precisa de todo erro de rede/CORS. |
| ANUI003 | Caiu aqui: renderização da tela / Failed here: page rendering | Componente React; aparece uma tela de recuperação com botão de recarregar. |
| ANUI004 | Falha assíncrona não tratada / Unhandled asynchronous failure | Promise sem tratamento; procurar o fluxo que não tratou a rejeição. |
| ANUI005 | Caiu aqui: inicialização do frontend / Failed here: frontend startup | Carregamento inicial, montagem React e importação da prévia. |
| ANUI006 | Erro de execução ou carregamento de recurso / Runtime or resource loading error | JavaScript, CSS, imagem ou outro recurso que falhou ao carregar. |

Os avisos do navegador registram apenas área da API, método, status e referências validadas. Não recebem o objeto Axios/Error nem imprimem URL, query ou formulário. Mensagens nativas do navegador, React, EF e provedores externos têm políticas próprias; o filtro de dados acima descreve os avisos adicionados neste trabalho.

## API — catálogo completo

O nível da tabela é o padrão do ponto; um registro HTTP de rejeição pode usar `Warning` para status 4xx, mesmo quando a etapa possui nível `Error`.

<!-- diagnostic-catalog -->

| Code | Level | Portuguese / English | Source and investigation |
| --- | --- | --- | --- |
| AN0001 | Critical | Caiu aqui: inicialização da API / Failed here: API startup | Program.cs: RunAsync; runtime, binding port, host startup |
| AN0002 | Information | API iniciou e está pronta / API started and is ready | Program.cs: ApplicationStarted; host readiness only |
| AN0101 | Critical | Sistema não validou a configuração ou os arquivos de segredo / System did not validate configuration or secret files | Program.cs: configuration, secret files, JWT policy |
| AN0102 | Critical | Caiu aqui: migração ou criação inicial do administrador / Failed here: migration or initial administrator setup | Program.cs: --migrate and initial administrator setup |
| AN0103 | Critical | Sistema não validou a conexão ou as permissões do banco / System did not validate the database connection or permissions | Program.cs / ApiExceptionHandler.cs: SQL TLS, availability and grants |
| AN0201 | Warning | Sistema não validou o login / System did not validate the login | AuthController.Login: credentials, account state and lockout |
| AN0202 | Warning | Sistema não validou a sessão ou o token / System did not validate the session or token | Program.cs / SessionValidator.cs: expiry, signature and revocation |
| AN0203 | Warning | Sistema não validou o cadastro ou o convite / System did not validate registration or invitation | AuthController.Register / AuthService.cs: invite validity and concurrent use |
| AN0301 | Error | Caiu aqui: gravação ou concorrência no banco / Failed here: database write or concurrency | ServerService.cs / ApiExceptionHandler.cs: SQL persistence and conflicts |
| AN0401 | Error | Caiu aqui: conexão autenticada com o node / Failed here: authenticated node connection | NodeService.TestNodeConnectionAsync / DockerClientFactory.cs: certificates, network, socket and timeout |
| AN0402 | Warning | Sistema não validou o cadastro de confiança do node / System did not validate node trust enrollment | DockerEnrollment.RequireNode / NodeService.cs: exact trusted HTTPS origin |
| AN0501 | Error | Caiu aqui: inspeção ou validação da imagem Docker / Failed here: Docker image inspection or validation | DockerService.CreateContainerAsyncCore: approved image, preload, managed label and implicit volumes |
| AN0502 | Error | Caiu aqui: criação ou validação do volume com quota / Failed here: quota volume creation or validation | DockerService.CreateContainerAsyncCore / DockerEnrollment.cs: quota driver and returned volume options |
| AN0503 | Error | Caiu aqui: criação do container / Failed here: container creation | DockerService.CreateContainerAsyncCore: container payload and daemon response |
| AN0504 | Error | Sistema não validou a identidade do container / System did not validate container identity | DockerService.ResolveAsync: container presence and server/node/owner labels |
| AN0505 | Error | Caiu aqui: operação de energia ou remoção do container / Failed here: container power or removal operation | DockerService.*ContainerAsync: start, stop, restart and removal |
| AN0506 | Warning | Caiu aqui: leitura dos logs do jogo / Failed here: game log retrieval | DockerService.GetContainerLogsAsync: Docker log stream and timeout |
| AN0507 | Warning | Caiu aqui: leitura das métricas do jogo / Failed here: game metrics retrieval | DockerService.GetContainerStatsAsync: metrics response and timeout |
| AN0508 | Warning | Caiu aqui: envio ao console do jogo / Failed here: game console input | DockerService.SendCommandAsync: state, input validation and stdin attach |
| AN0601 | Warning | Sistema não validou recursos, capacidade ou porta do servidor / System did not validate server resources, capacity or port | SecurityLimits.Resources / ServerService.CreateServerAsync: capacity and unique port |
| AN0602 | Error | Caiu aqui: provisionamento do servidor / Failed here: server provisioning | ServerService.CreateServerAsync / DockerService.CreateContainerAsync: stage propagation |
| AN0603 | Warning | Caiu aqui: aquisição da trava de operação do servidor / Failed here: server operation lock acquisition | ServerOperationGate.AcquireAsync: SQL application lock or waiting timeout |
| AN0701 | Warning | Diretório de cartridges não encontrado / Cartridge directory was not found | CartridgeService.LoadCartridges: configured directory is absent |
| AN0702 | Error | Caiu aqui: leitura ou interpretação do cartridge / Failed here: cartridge reading or parsing | CartridgeService.LoadCartridges / ServerService.cs: missing, empty or invalid cartridge JSON |
| AN0703 | Warning | Sistema não validou as variáveis do cartridge / System did not validate cartridge variables | CartridgeService.ValidateVariables / ServerService.cs: required values, types and regex timeout |
| AN0801 | Warning | Sistema não validou a requisição / System did not validate the request | RequestDiagnosticMiddleware / ApiExceptionHandler.cs: HTTP 400/409 and rejected models |
| AN0802 | Warning | Acesso ao recurso foi negado / Resource access was denied | RequestDiagnosticMiddleware: HTTP 403, owner and role checks |
| AN0803 | Warning | Recurso ou rota não encontrado / Resource or route was not found | RequestDiagnosticMiddleware: HTTP 404, resource or route |
| AN0804 | Warning | Limite de requisições atingido / Request limit was reached | RequestDiagnosticMiddleware / Program.cs: HTTP 429, request and concurrency limits |
| AN0805 | Error | Dependência indisponível ou tempo esgotado / Dependency unavailable or timed out | RequestDiagnosticMiddleware / ApiExceptionHandler.cs: HTTP 502/503/504 |
| AN0806 | Error | Caiu aqui: falha não prevista na API / Failed here: unexpected API failure | ApiExceptionHandler / RequestDiagnosticMiddleware: unmapped or unhandled API failure |
| AN0807 | Warning | Requisição demorou além do limite de diagnóstico / Request exceeded the diagnostic duration threshold | RequestDiagnosticMiddleware: Diagnostics:SlowRequestMilliseconds |
| AN0808 | Warning | Cliente interrompeu a requisição / Client aborted the request | RequestDiagnosticMiddleware: client disconnect or aborted HTTP request |
| AN0901 | Error | Caiu aqui: gravação da auditoria / Failed here: audit persistence | AuditLogMiddleware.WriteAuditLogAsync: audit DB write; main request is preserved |
