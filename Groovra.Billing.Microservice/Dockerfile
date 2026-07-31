# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# curl - лише для docker-compose healthcheck (CMD curl), базовий образ його не має.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
# /app/data має існувати в образі з правильним власником ДО того, як туди змонтується
# groovra-billing-data-volume: порожній named volume при першому підключенні успадковує
# вміст/права з цього шляху в образі. Без цього Docker сам створює точку монтування як
# root, і non-root $APP_UID не може писати SQLite-файл -> "unable to open database file".
RUN mkdir -p /app/data && chown $APP_UID:$APP_UID /app/data
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Groovra.Billing.Microservice/Groovra.Billing.Microservice.csproj", "Groovra.Billing.Microservice/"]
COPY ["Groovra.Shared/Groovra.Shared.csproj", "Groovra.Shared/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Groovra.Billing.Microservice/Groovra.Billing.Microservice.csproj" -r linux-x64
COPY . .
WORKDIR "/src/Groovra.Billing.Microservice"

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./Groovra.Billing.Microservice.csproj" -c $BUILD_CONFIGURATION -o /app/publish -r linux-x64 --self-contained false /p:UseAppHost=false /p:DebugType=none

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Groovra.Billing.Microservice.dll"]
