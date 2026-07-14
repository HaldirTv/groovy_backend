FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
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
RUN dotnet restore "Groovra.History.Microservice/Groovra.History.Microservice.csproj"
COPY . .
WORKDIR "/src/Groovra.History.Microservice"
RUN dotnet build "./Groovra.History.Microservice.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Groovra.History.Microservice.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Groovra.History.Microservice.dll"]
