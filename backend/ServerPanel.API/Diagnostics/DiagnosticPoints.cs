namespace ServerPanel.API.Diagnostics;

public sealed record DiagnosticPoint(int Number, LogLevel Level, string Portuguese, string English)
{
    public string Code => $"AN{Number:D4}";
}

public static class DiagnosticPoints
{
    public static readonly DiagnosticPoint Startup = new(1, LogLevel.Critical, "Caiu aqui: inicialização da API", "Failed here: API startup");
    public static readonly DiagnosticPoint Ready = new(2, LogLevel.Information, "API iniciou e está pronta", "API started and is ready");
    public static readonly DiagnosticPoint Configuration = new(101, LogLevel.Critical, "Sistema não validou a configuração ou os arquivos de segredo", "System did not validate configuration or secret files");
    public static readonly DiagnosticPoint Migration = new(102, LogLevel.Critical, "Caiu aqui: migração ou criação inicial do administrador", "Failed here: migration or initial administrator setup");
    public static readonly DiagnosticPoint DatabaseConnection = new(103, LogLevel.Critical, "Sistema não validou a conexão ou as permissões do banco", "System did not validate the database connection or permissions");
    public static readonly DiagnosticPoint Login = new(201, LogLevel.Warning, "Sistema não validou o login", "System did not validate the login");
    public static readonly DiagnosticPoint Session = new(202, LogLevel.Warning, "Sistema não validou a sessão ou o token", "System did not validate the session or token");
    public static readonly DiagnosticPoint Registration = new(203, LogLevel.Warning, "Sistema não validou o cadastro ou o convite", "System did not validate registration or invitation");
    public static readonly DiagnosticPoint DatabaseWrite = new(301, LogLevel.Error, "Caiu aqui: gravação ou concorrência no banco", "Failed here: database write or concurrency");
    public static readonly DiagnosticPoint NodeConnection = new(401, LogLevel.Error, "Caiu aqui: conexão autenticada com o node", "Failed here: authenticated node connection");
    public static readonly DiagnosticPoint NodeEnrollment = new(402, LogLevel.Warning, "Sistema não validou o cadastro de confiança do node", "System did not validate node trust enrollment");
    public static readonly DiagnosticPoint DockerImage = new(501, LogLevel.Error, "Caiu aqui: inspeção ou validação da imagem Docker", "Failed here: Docker image inspection or validation");
    public static readonly DiagnosticPoint DockerVolume = new(502, LogLevel.Error, "Caiu aqui: criação ou validação do volume com quota", "Failed here: quota volume creation or validation");
    public static readonly DiagnosticPoint DockerCreate = new(503, LogLevel.Error, "Caiu aqui: criação do container", "Failed here: container creation");
    public static readonly DiagnosticPoint DockerIdentity = new(504, LogLevel.Error, "Sistema não validou a identidade do container", "System did not validate container identity");
    public static readonly DiagnosticPoint DockerPower = new(505, LogLevel.Error, "Caiu aqui: operação de energia ou remoção do container", "Failed here: container power or removal operation");
    public static readonly DiagnosticPoint DockerLogs = new(506, LogLevel.Warning, "Caiu aqui: leitura dos logs do jogo", "Failed here: game log retrieval");
    public static readonly DiagnosticPoint DockerStats = new(507, LogLevel.Warning, "Caiu aqui: leitura das métricas do jogo", "Failed here: game metrics retrieval");
    public static readonly DiagnosticPoint DockerConsole = new(508, LogLevel.Warning, "Caiu aqui: envio ao console do jogo", "Failed here: game console input");
    public static readonly DiagnosticPoint ServerAdmission = new(601, LogLevel.Warning, "Sistema não validou recursos, capacidade ou porta do servidor", "System did not validate server resources, capacity or port");
    public static readonly DiagnosticPoint Provisioning = new(602, LogLevel.Error, "Caiu aqui: provisionamento do servidor", "Failed here: server provisioning");
    public static readonly DiagnosticPoint ServerLock = new(603, LogLevel.Warning, "Caiu aqui: aquisição da trava de operação do servidor", "Failed here: server operation lock acquisition");
    public static readonly DiagnosticPoint CartridgeDirectory = new(701, LogLevel.Warning, "Diretório de cartridges não encontrado", "Cartridge directory was not found");
    public static readonly DiagnosticPoint CartridgeLoad = new(702, LogLevel.Error, "Caiu aqui: leitura ou interpretação do cartridge", "Failed here: cartridge reading or parsing");
    public static readonly DiagnosticPoint CartridgeVariables = new(703, LogLevel.Warning, "Sistema não validou as variáveis do cartridge", "System did not validate cartridge variables");
    public static readonly DiagnosticPoint Validation = new(801, LogLevel.Warning, "Sistema não validou a requisição", "System did not validate the request");
    public static readonly DiagnosticPoint AccessDenied = new(802, LogLevel.Warning, "Acesso ao recurso foi negado", "Resource access was denied");
    public static readonly DiagnosticPoint NotFound = new(803, LogLevel.Warning, "Recurso ou rota não encontrado", "Resource or route was not found");
    public static readonly DiagnosticPoint RateLimited = new(804, LogLevel.Warning, "Limite de requisições atingido", "Request limit was reached");
    public static readonly DiagnosticPoint Unavailable = new(805, LogLevel.Error, "Dependência indisponível ou tempo esgotado", "Dependency unavailable or timed out");
    public static readonly DiagnosticPoint Unexpected = new(806, LogLevel.Error, "Caiu aqui: falha não prevista na API", "Failed here: unexpected API failure");
    public static readonly DiagnosticPoint SlowRequest = new(807, LogLevel.Warning, "Requisição demorou além do limite de diagnóstico", "Request exceeded the diagnostic duration threshold");
    public static readonly DiagnosticPoint RequestAborted = new(808, LogLevel.Warning, "Cliente interrompeu a requisição", "Client aborted the request");
    public static readonly DiagnosticPoint AuditWrite = new(901, LogLevel.Error, "Caiu aqui: gravação da auditoria", "Failed here: audit persistence");

    public static readonly IReadOnlyList<DiagnosticPoint> All = new[] {
        Startup, Ready, Configuration, Migration, DatabaseConnection, Login, Session, Registration,
        DatabaseWrite, NodeConnection, NodeEnrollment, DockerImage, DockerVolume, DockerCreate,
        DockerIdentity, DockerPower, DockerLogs, DockerStats, DockerConsole, ServerAdmission,
        Provisioning, ServerLock, CartridgeDirectory, CartridgeLoad, CartridgeVariables,
        Validation, AccessDenied, NotFound, RateLimited, Unavailable, Unexpected, SlowRequest, RequestAborted, AuditWrite
    };
}
