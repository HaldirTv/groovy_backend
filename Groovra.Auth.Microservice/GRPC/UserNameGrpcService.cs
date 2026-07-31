using Grpc.Core;
using Groovra.Shared.Grpc;
using Groovra.Auth.Microservice.Data;
using Microsoft.EntityFrameworkCore;

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
                // DisplayName живе в Profile і може бути відсутнім/порожнім (профіль ще не
                // створено) - тоді чесно віддаємо username, щоб споживач ніколи не отримав
                // порожнє ім'я замість імені автора.
                var displayName = await _dbContext.Profiles
                    .AsNoTracking()
                    .Where(p => p.UserId == userId)
                    .Select(p => p.DisplayName)
                    .FirstOrDefaultAsync(context.CancellationToken);

                return new GetUserNameGrpcResponse
                {
                    Username = user.Username,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? user.Username : displayName
                };
            }
        }
        throw new RpcException(new Status(StatusCode.NotFound, $"User with ID {request.UserId} not found."));
    }
}