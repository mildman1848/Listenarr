using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

[ApiController]
[Route("api/v{version:apiVersion}/rootfolder-relocations")]
[Tags("Root Folder Relocations")]
public sealed class RootFolderRelocationsController(IRootFolderRelocationService relocationService)
    : ControllerBase
{
    public sealed record ReauthorizeLegacyTargetRequest(string ConfirmedTargetPath);

    [HttpGet("{id:guid}", Name = "GetRootFolderRelocation")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await relocationService.GetAsync(id, cancellationToken);
        return result == null
            ? NotFound(new { message = "Root folder relocation not found" })
            : Ok(result);
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await relocationService.RetryAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { message = exception.Message });
        }
    }

    [HttpPost("{id:guid}/reauthorize-legacy-target")]
    public async Task<IActionResult> ReauthorizeLegacyTarget(
        Guid id,
        [FromBody] ReauthorizeLegacyTargetRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await relocationService.ReauthorizeLegacyTargetAsync(
                id,
                request.ConfirmedTargetPath,
                cancellationToken));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { message = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }
}
