FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Groovra.ApiGateway/Groovra.ApiGateway.csproj", "Groovra.ApiGateway/"]
COPY ["Groovra.Shared/Groovra.Shared.csproj", "Groovra.Shared/"]
RUN dotnet restore "Groovra.ApiGateway/Groovra.ApiGateway.csproj"
COPY . .
WORKDIR "/src/Groovra.ApiGateway"
RUN dotnet build "./Groovra.ApiGateway.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Groovra.ApiGateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Groovra.ApiGateway.dll"]
