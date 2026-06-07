using Grpc.Core;
using Groovra.Shared.Grpc;
using Groovra.Auth.Microservice.Data;

namespace Groovra.Auth.Microservice.GRPC;

public class UserNameGrpcService : Groovra.Shared.Grpc.UserNameGrpcService.UserNameGrpcServiceBase
{
    private readonly AuthDbContext _dbContext;

    public UserNameGrpcService(AuthDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public override async Task<GetUserNameGrpcResponse> GetUserNameGrpc(UserNameGrpcRequest request, ServerCallContext context)
    {
        if (Guid.TryParse(request.UserId, out Guid userId))
        {
            var user = await _dbContext.Users.FindAsync(new object[] { userId }, context.CancellationToken);
            if (user != null)
            {
                return new GetUserNameGrpcResponse { Username = user.Username };
            }
        }
        throw new RpcException(new Status(StatusCode.NotFound, $"User with ID {request.UserId} not found."));
    }
}
