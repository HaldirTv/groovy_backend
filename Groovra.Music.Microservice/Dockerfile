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
RUN dotnet restore "Groovra.Music.Microservice/Groovra.Music.Microservice.csproj"
COPY . .
WORKDIR "/src/Groovra.Music.Microservice"
RUN dotnet build "./Groovra.Music.Microservice.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Groovra.Music.Microservice.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Groovra.Music.Microservice.dll"]
