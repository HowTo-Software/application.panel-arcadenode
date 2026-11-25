using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Common;
using ServerPanel.API.Data;
using ServerPanel.API.DTOs;
using ServerPanel.API.Models;
using ServerPanel.API.Services;

namespace ServerPanel.API.Controllers;

[ApiController]
[Route("api/admin/egg-catalog")]
[Authorize(Roles = "Admin,SuperAdmin")]
public sealed class EggsController(PanelDbContext db, EggRuntimeProfiles profiles) : ControllerBase
{
    [HttpGet("nests")]
    public async Task<ActionResult<IReadOnlyList<NestSummary>>> GetNests(
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var (p, size) = Pagination.Resolve(page, pageSize);
        var total = await db.Nests.CountAsync(cancellationToken);
        var items = await db.Nests.OrderBy(n => n.Name).ThenBy(n => n.Id)
            .Skip(Pagination.Skip(p, size)).Take(size)
            .Select(n => new NestSummary(n.Id, n.Name, n.Description, n.Eggs.Count(e => e.PterodactylSha256 != null)))
            .ToListAsync(cancellationToken);
        Response.ApplyPaginationHeaders(new PagedResult<NestSummary>(items, p, size, total));
        return Ok(items);
    }

    [HttpPost("nests")]
    public async Task<ActionResult<NestSummary>> CreateNest(CreateNestRequest request, CancellationToken cancellationToken)
    {
        var nest = new Nest { Name = request.Name.Trim(), Description = request.Description?.Trim() ?? "" };
        db.Nests.Add(nest);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetEggs), new { nestId = nest.Id }, new NestSummary(nest.Id, nest.Name, nest.Description, 0));
    }

    [HttpGet("nests/{nestId:guid}/eggs")]
    public async Task<ActionResult<IReadOnlyList<ImportedEggSummary>>> GetEggs(
        Guid nestId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        if (!await db.Nests.AnyAsync(n => n.Id == nestId, cancellationToken)) return NotFound();

        var (p, size) = Pagination.Resolve(page, pageSize);
        var eggs = db.Eggs.Where(e => e.NestId == nestId && e.PterodactylSha256 != null);
        var total = await eggs.CountAsync(cancellationToken);
        var items = await eggs.OrderBy(e => e.Name).ThenBy(e => e.Id)
            .Skip(Pagination.Skip(p, size)).Take(size)
            .Select(e => new ImportedEggSummary(e.Id, e.NestId, e.Name, e.Description, e.CreatedAt, e.PterodactylSha256!))
            .ToListAsync(cancellationToken);
        Response.ApplyPaginationHeaders(new PagedResult<ImportedEggSummary>(items, p, size, total));
        return Ok(items.Select(WithRuntime));
    }

    [HttpGet("eggs/{id:guid}/runtime")]
    public async Task<IActionResult> GetRuntime(Guid id, CancellationToken token)
    {
        var egg = await db.Eggs.Where(e => e.Id == id).Select(e => new { e.PterodactylSha256, e.Variables }).SingleOrDefaultAsync(token);
        if (egg?.PterodactylSha256 is not { } digest) return NotFound();
        var runtime = profiles.Resolve(digest, egg.Variables);
        return Ok(new { sha256 = digest, cartridgeId = runtime?.Cartridge.Id, variables = runtime?.DescribeVariables() });
    }

    [HttpPost("nests/{nestId:guid}/eggs/import")]
    [Consumes("application/json")]
    public async Task<ActionResult<ImportedEggSummary>> Import(Guid nestId, [FromBody] JsonElement manifest, CancellationToken cancellationToken)
    {
        if (!await db.Nests.AnyAsync(n => n.Id == nestId, cancellationToken)) return NotFound();

        var egg = PterodactylEggParser.Parse(manifest, nestId);
        var matching = db.Eggs.AsNoTracking().Where(e => e.NestId == nestId && e.PterodactylSha256 == egg.PterodactylSha256)
            .Select(e => new ImportedEggSummary(e.Id, e.NestId, e.Name, e.Description, e.CreatedAt, e.PterodactylSha256!));
        var existing = await matching.SingleOrDefaultAsync(cancellationToken);
        if (existing != null) return Ok(WithRuntime(existing));

        db.Eggs.Add(egg);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(egg).State = EntityState.Detached;
            existing = await matching.SingleOrDefaultAsync(cancellationToken);
            if (existing != null) return Ok(WithRuntime(existing));
            throw;
        }

        return CreatedAtAction(nameof(Export), new { id = egg.Id },
            WithRuntime(new ImportedEggSummary(egg.Id, nestId, egg.Name, egg.Description, egg.CreatedAt, egg.PterodactylSha256!)));
    }

    private ImportedEggSummary WithRuntime(ImportedEggSummary summary) =>
        summary with { CartridgeId = profiles.ResolveCartridge(summary.Sha256)?.Id };

    [HttpGet("eggs/{id:guid}/export")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Export(Guid id, CancellationToken cancellationToken)
    {
        var manifest = await db.Eggs.Where(e => e.Id == id).Select(e => e.PterodactylManifest).SingleOrDefaultAsync(cancellationToken);
        if (manifest == null) return NotFound();
        return File(Encoding.UTF8.GetBytes(manifest), "application/json", $"egg-{id}.json");
    }
}

public sealed record CreateNestRequest([Required, StringLength(100)] string Name, [StringLength(500)] string? Description);
public sealed record NestSummary(Guid Id, string Name, string Description, int ImportedEggCount);
public sealed record ImportedEggSummary(Guid Id, Guid NestId, string Name, string Description, DateTime ImportedAt, string Sha256,
    string? CartridgeId = null);
