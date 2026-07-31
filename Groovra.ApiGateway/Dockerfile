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
RUN dotnet restore "Groovra.ApiGateway/Groovra.ApiGateway.csproj" -r linux-x64
COPY . .
WORKDIR "/src/Groovra.ApiGateway"

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# -r linux-x64 --self-contained false: контейнер завжди linux-x64, зайві RID не потрібні.
# /p:DebugType=none: не генерувати .pdb у проді - менше файлів для копіювання й місця на диску.
# Прибрано окремий "dotnet build -o /app/build" - фінальний образ бере лише /app/publish.
RUN dotnet publish "./Groovra.ApiGateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish -r linux-x64 --self-contained false /p:UseAppHost=false /p:DebugType=none

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Groovra.ApiGateway.dll"]
