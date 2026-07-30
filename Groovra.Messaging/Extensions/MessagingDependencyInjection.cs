using System.Reflection;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Groovra.Messaging.Extensions;
public static class MessagingDependencyInjection
{
    public static IServiceCollection AddMessagingBus(
        this IServiceCollection services, 
        IConfiguration configuration,
        Assembly? consumersAssembly = null)
    {
        services.AddMassTransit(x =>
        {
            if (consumersAssembly != null)
            {
                x.AddConsumers(consumersAssembly);
            }

            var host = configuration["RabbitMQ:Host"] ?? "none";
            var virtualHost = configuration["RabbitMQ:VirtualHost"] ?? "/";
            var username = configuration["RabbitMQ:Username"] ?? "guest";
            var password = configuration["RabbitMQ:Password"] ?? "guest";

            if (string.IsNullOrWhiteSpace(host) || host.Equals("none", StringComparison.OrdinalIgnoreCase) || host.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            {
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.ConfigureEndpoints(context);
                });
            }
            else
            {
                x.UsingRabbitMq((context, cfg) =>
                {
                    cfg.Host(host, virtualHost, h =>
                    {
                        h.Username(username);
                        h.Password(password);
                    });

                    cfg.ConfigureEndpoints(context);
                });
            }

        });

        return services;
    }
}