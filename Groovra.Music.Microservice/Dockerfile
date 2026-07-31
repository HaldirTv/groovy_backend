# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Groovra.Music.Microservice/Groovra.Music.Microservice.csproj", "Groovra.Music.Microservice/"]
COPY ["Groovra.Shared/Groovra.Shared.csproj", "Groovra.Shared/"]
COPY ["Groovra.Messaging/Groovra.Messaging.csproj", "Groovra.Messaging/"]
# Кеш NuGet-пакетів переживає між збірками РІЗНИХ сервісів (не лежить у шарах образу,
# тому не роздуває його розмір) - спільні залежності (EF Core, Grpc.Tools і т.д.)
# скачуються один раз, а не по колу для кожного з 6 сервісів.
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Groovra.Music.Microservice/Groovra.Music.Microservice.csproj" -r linux-x64
COPY . .
WORKDIR "/src/Groovra.Music.Microservice"

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# -r linux-x64 --self-contained false: без цього Whisper.net.AllRuntimes копіює нативні
# бібліотеки під ВСІ підтримувані рантайми (linux-arm64/arm/x64 і т.д.) - на 10GB дроплеті
# це саме те, що забило диск і зламало білд. Контейнер завжди linux-x64, інше не потрібне.
# Прибрано також окремий "dotnet build -o /app/build" крок - фінальний образ бере файли
# лише з /app/publish, тож проміжний build дублював ту саму важку копію натівів даремно.
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./Groovra.Music.Microservice.csproj" -c $BUILD_CONFIGURATION -o /app/publish -r linux-x64 --self-contained false /p:UseAppHost=false /p:DebugType=none

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Groovra.Music.Microservice.dll"]
