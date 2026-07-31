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
            : Ok(RootFolderRelocationPublicProjection.Sanitize(result));
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(RootFolderRelocationPublicProjection.Sanitize(
                await relocationService.RetryAsync(id, cancellationToken)));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "Root folder relocation not found" });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new
            {
                message = "The relocation cannot be retried in its current state."
            });
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
            return Ok(RootFolderRelocationPublicProjection.Sanitize(
                await relocationService.ReauthorizeLegacyTargetAsync(
                    id,
                    request.ConfirmedTargetPath,
                    cancellationToken)));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "Root folder relocation not found" });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new
            {
                message = "The relocation target cannot be reauthorized in its current state."
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new
            {
                message = "The confirmed relocation target is invalid."
            });
        }
    }
}
