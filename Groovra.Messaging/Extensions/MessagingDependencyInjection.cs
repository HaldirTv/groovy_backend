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
        Assembly? consumersAssembly = null,
        string? endpointPrefix = null)
    {
        services.AddMassTransit(x =>
        {
            if (consumersAssembly != null)
            {
                x.AddConsumers(consumersAssembly);
            }

            // Consumer classes are only unique per-assembly, not globally (e.g. Music and Chat both
            // have a UserNicknameChangedConsumer). The default formatter names queues after the consumer
            // class alone, so without a per-service prefix two services would bind the same queue name
            // and silently become competing consumers of each other's events.
            var formatter = endpointPrefix != null
                ? new KebabCaseEndpointNameFormatter(endpointPrefix, false)
                : KebabCaseEndpointNameFormatter.Instance;

            var host = configuration["RabbitMQ:Host"] ?? "none";
            var virtualHost = configuration["RabbitMQ:VirtualHost"] ?? "/";
            var username = configuration["RabbitMQ:Username"] ?? "guest";
            var password = configuration["RabbitMQ:Password"] ?? "guest";

            if (string.IsNullOrWhiteSpace(host) || host.Equals("none", StringComparison.OrdinalIgnoreCase) || host.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            {
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.ConfigureEndpoints(context, formatter);
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

                    cfg.ConfigureEndpoints(context, formatter);
                });
            }

        });

        return services;
    }
}