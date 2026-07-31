# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# curl - лише для docker-compose healthcheck (CMD curl), базовий образ його не має.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Groovra.History.Microservice/Groovra.History.Microservice.csproj", "Groovra.History.Microservice/"]
COPY ["Groovra.Messaging/Groovra.Messaging.csproj", "Groovra.Messaging/"]
COPY ["Groovra.Shared/Groovra.Shared.csproj", "Groovra.Shared/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Groovra.History.Microservice/Groovra.History.Microservice.csproj" -r linux-x64
COPY . .
WORKDIR "/src/Groovra.History.Microservice"

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./Groovra.History.Microservice.csproj" -c $BUILD_CONFIGURATION -o /app/publish -r linux-x64 --self-contained false /p:UseAppHost=false /p:DebugType=none

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Groovra.History.Microservice.dll"]
