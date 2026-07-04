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
            // Если передан assembly, значит этот микросервис является потребителем (History)
            // Он автоматически найдет и зарегистрирует все классы, унаследованные от IConsumer
            if (consumersAssembly != null)
            {
                x.AddConsumers(consumersAssembly);
            }

            x.UsingRabbitMq((context, cfg) =>
            {
                // Читаем настройки из appsettings.json, если их нет — берем дефолт
                var host = configuration["RabbitMQ:Host"] ?? "localhost";
                var virtualHost = configuration["RabbitMQ:VirtualHost"] ?? "/";
                var username = configuration["RabbitMQ:Username"] ?? "guest";
                var password = configuration["RabbitMQ:Password"] ?? "guest";

                cfg.Host(host, virtualHost, h =>
                {
                    h.Username(username);
                    h.Password(password);
                });

                // Автоматически настраивает эндпоинты для всех зарегистрированных консьюмеров
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}