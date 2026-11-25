using ServerPanel.API.DTOs;

namespace ServerPanel.API.Services;

public interface INodeService
{
    Task<PagedResult<NodeDto>> GetAllNodesAsync(int page = 1, int pageSize = 50);
    Task<NodeDto?> GetNodeByIdAsync(Guid id);
    Task<NodeDto?> CreateNodeAsync(CreateNodeRequest request);
    Task<NodeDto?> UpdateNodeAsync(Guid id, UpdateNodeRequest request);
    Task<bool> DeleteNodeAsync(Guid id);
    Task<bool> UpdateNodeStatusAsync(Guid id, bool isOnline);
    Task<PagedResult<AllocationDto>> GetNodeAllocationsAsync(Guid nodeId, int page = 1, int pageSize = 50);
    Task<bool> CreateAllocationsAsync(CreateAllocationRequest request);
    Task<bool> DeleteAllocationAsync(Guid id);
    Task<NodeConnectionTestResult> TestNodeConnectionAsync(string dockerEndpoint);
    Task<NodeConnectionTestResult> TestNodeConnectionByIdAsync(Guid nodeId);
}
