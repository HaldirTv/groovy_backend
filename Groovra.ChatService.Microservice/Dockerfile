FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Groovra.ChatService.Microservice/Groovra.ChatService.Microservice.csproj", "Groovra.ChatService.Microservice/"]
COPY ["Groovra.Shared/Groovra.Shared.csproj", "Groovra.Shared/"]
RUN dotnet restore "Groovra.ChatService.Microservice/Groovra.ChatService.Microservice.csproj"
COPY . .
WORKDIR "/src/Groovra.ChatService.Microservice"
RUN dotnet build "./Groovra.ChatService.Microservice.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Groovra.ChatService.Microservice.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Groovra.ChatService.Microservice.dll"]
