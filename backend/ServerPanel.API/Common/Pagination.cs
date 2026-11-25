using ServerPanel.API.DTOs;

namespace ServerPanel.API.Common;

/// <summary>
/// Every list is paged: 50 records by default and at most 200 per request.
/// </summary>
public static class Pagination
{
    public const int MaxPageSize = 200;

    /// <summary>
    /// Resolves the bounded effective (page, pageSize).
    /// </summary>
    public static (int Page, int PageSize) Resolve(int? page, int? pageSize)
    {
        var resolvedPage = page.GetValueOrDefault(1);
        if (resolvedPage < 1) resolvedPage = 1;

        return (Math.Clamp(resolvedPage, 1, 1000000), Math.Clamp(pageSize ?? 50, 1, MaxPageSize));
    }

    /// <summary>Computes an offset from the bounded resolved page values.</summary>
    public static int Skip(int page, int pageSize) =>
        checked((page - 1) * pageSize);
}

public static class PaginationHttpExtensions
{
    public static void ApplyPaginationHeaders<T>(this HttpResponse response, PagedResult<T> result)
    {
        response.Headers["X-Total-Count"] = result.TotalCount.ToString();
        response.Headers["X-Page"] = result.Page.ToString();
        response.Headers["X-Page-Size"] =
            (result.PageSize == int.MaxValue ? result.TotalCount : result.PageSize).ToString();
    }
}
