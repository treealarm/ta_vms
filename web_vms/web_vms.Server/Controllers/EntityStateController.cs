using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;
using web_vms.Types.Dto;
using Vms.Utils;

namespace web_vms
{
  [Route("api/[controller]")]
  [ApiController]
  public class EntityStateController(UtilsService.UtilsServiceClient grpcClient) : ControllerBase
  {
    [HttpGet]
    public async Task<IActionResult> GetEntityStates(CancellationToken ct)
    {
      var resp = await grpcClient.ListEntityStatesAsync(new Empty(), cancellationToken: ct);
      return Ok(resp.Entities.Select(e => new EntityStateDto
      {
        Service = e.Service,
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        StateJson = e.StateJson,
        LastSeen = e.LastSeen?.ToDateTime() ?? DateTime.UnixEpoch,
        Stale = e.Stale
      }).ToList());
    }
  }
}
