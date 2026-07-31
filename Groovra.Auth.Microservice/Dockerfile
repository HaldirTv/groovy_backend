# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# curl - лише для docker-compose healthcheck (CMD curl), базовий образ його не має.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
# Та сама причина, що й у Music: groovra-auth-media-volume монтується в /app/MediaStorage,
# і без наявної в образі теки з правильним власником Docker створить її від root — аватари
# та банери користувачів не збережуться.
RUN mkdir -p /app/MediaStorage \
    && chown -R $APP_UID:$APP_UID /app/MediaStorage
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Groovra.Auth.Microservice/Groovra.Auth.Microservice.csproj", "Groovra.Auth.Microservice/"]
COPY ["Groovra.Shared/Groovra.Shared.csproj", "Groovra.Shared/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Groovra.Auth.Microservice/Groovra.Auth.Microservice.csproj" -r linux-x64
COPY . .
WORKDIR "/src/Groovra.Auth.Microservice"

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./Groovra.Auth.Microservice.csproj" -c $BUILD_CONFIGURATION -o /app/publish -r linux-x64 --self-contained false /p:UseAppHost=false /p:DebugType=none

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Groovra.Auth.Microservice.dll"]
