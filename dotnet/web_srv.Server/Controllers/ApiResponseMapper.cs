using Vms.Control;

namespace web_srv;

public record ApiResponse(bool Success, string Message);

public static class ApiResponseMapper
{
  public static ApiResponse ToApiResponse(OperationStatus status) => new(status.Success, status.Message);
}
