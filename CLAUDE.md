# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Groovra is a .NET 10 microservices backend for a music streaming platform (auth, music catalog/uploads, listen history, billing), fronted by a YARP reverse-proxy API gateway. There is no frontend in this repo.

## Solution layout

- `Groovra.ApiGateway` — YARP reverse proxy. The only service that terminates JWT auth; all client traffic enters here.
- `Groovra.Auth.Microservice` — users, profiles, roles, Google OAuth, JWT issuance. Owns the `auth` schema.
- `Groovra.Music.Microservice` — tracks, albums, playlists, favorites, uploads, Jamendo import, Hangfire jobs. Owns the `music` schema.
- `Groovra.History.Microservice` — listen history, consumes `TrackPlayedEvent` off RabbitMQ. Owns the `history` schema.
- `Groovra.Billing.Microservice` — scaffold only (default WeatherForecast template), not yet implemented.
- `Groovra.Shared` — cross-service constants/extensions (`AppRoles`, `HeaderNames`, `HttpContextExtensions`, `ServiceResult<T>`) and generated gRPC client/server code from `/Protos`.
- `Groovra.Messaging` — MassTransit/RabbitMQ DI setup (`AddMessagingBus`) and shared event contracts (e.g. `TrackPlayedEvent`).
- `/Protos` — shared `.proto` definitions (`trackinfo.proto`, `username.proto`) referenced by multiple services for codegen.

## Build & run

```
dotnet build Groovra.sln                                   # build everything
dotnet build Groovra.Music.Microservice                    # build one service
dotnet run --project Groovra.Auth.Microservice              # run one service (https profile from launchSettings)
docker compose up --build                                   # run full stack (redis, rabbitmq, gateway + 3 services)
```

There are no automated test projects in this solution currently.

Local HTTPS ports per launchSettings (also reflected in the gateway's `ReverseProxy:Clusters` config in `Groovra.ApiGateway/appsettings.json`):
- Gateway: `https://localhost:7005`
- Auth: `https://localhost:7008`
- Music: `https://localhost:7176`
- History: `https://localhost:7232`

Do not set `RuntimeIdentifier`/`RuntimeIdentifiers` globally — `Directory.Build.props` documents why (breaks local Windows debugging under the IDE). Only pin a RID at `dotnet publish` time.

## Cross-service architecture

**Auth flow**: the gateway is the only service that validates JWTs (`AddJwtBearer` in `Groovra.ApiGateway/Program.cs`). On every authenticated request it strips any incoming `X-User-*` headers and re-adds `X-User-Id`, `X-User-Name` (URL-encoded), and `X-User-Role` (comma-joined, defaults to `Listener`) from the validated JWT claims before proxying downstream. Downstream services (Auth/Music/History) **never see or validate the JWT** — they trust these headers directly via `HttpContext.TryGetUserId()`, `.GetUserName()`, and `.UserIsInRole(...)` extensions in `Groovra.Shared.Extensions.HttpContextExtensions`. When adding a new downstream endpoint that needs the caller's identity, read these headers rather than re-implementing JWT parsing.

**Gateway routing & authorization** (`Groovra.ApiGateway/appsettings.json`, `ReverseProxy:Routes`): route order matters — more specific routes (e.g. `music-tracks-modify` for mutating verbs, `music-jamendo`) are declared with lower `Order` than the catch-all `music-route`, and each carries its own `AuthorizationPolicy` (`AdminOnly`, `ArtistOnly`, `Default`, or none for public GETs). When adding a new music/history endpoint that needs different auth than its siblings, add a new route with an explicit `Order` and policy rather than relying on the catch-all.

**Shared database, per-service schema**: Auth, Music, and History all connect to the same SQL Server database via `ConnectionStrings:DefaultConnection`, but each owns its own EF Core schema (`auth`, `music`, `history` respectively, set via `modelBuilder.HasDefaultSchema(...)` in each `*DbContext`) and its own `Migrations/` folder. Never cross schemas directly from another service's code — cross-service data access goes through gRPC or events instead.

**Service-to-service calls are gRPC, driven by `/Protos`**:
- Music → Auth: `UserNameGrpcService` (resolve a user ID to username).
- History → Music: `TrackInfoGrpcService` (resolve track IDs to full track details for history responses).
Clients are registered with `AddGrpcClient<...>` and point at the other service's HTTPS URL (`AuthGrpcUrl` / `MusicGrpcUrl` in config), with `DangerousAcceptAnyServerCertificate` for local dev self-signed certs — do not carry that into a real production TLS setup without revisiting it.

**Async events go through RabbitMQ via MassTransit** (`Groovra.Messaging`): Music publishes `TrackPlayedEvent` when a track is streamed; History's `TrackPlayedConsumer` consumes it and writes a `PlaybackHistory` row. `AddMessagingBus(config, assembly)` auto-registers `IConsumer<T>` classes when an assembly is passed (History), and is publish-only when it isn't (Music). New async cross-service side effects should follow this producer/consumer pattern rather than direct HTTP/gRPC calls.

**Media storage**: uploaded audio/covers/album covers live under each service's own `MediaStorage/` folder (or the path in `MediaStorage:BasePath` in Docker, backed by the `groovra-media-volume`), served back out via `app.UseStaticFiles` at `/media` (Auth) or `/music/files/...` (Music). The Music service also runs a daily Hangfire job (`GarbageCollectorService.CleanUpGarbageAsync`, `groovra-music-garbage-cleanup`) to reconcile orphaned files.

**API docs**: each service exposes its own OpenAPI doc via Scalar in Development; the gateway aggregates them at `/scalar/v1` by proxying `/docs/{service}/openapi.json` through YARP's `PathPattern` transform back to each service's `/openapi/v1.json`.

## Conventions to be aware of

- Comments and some identifiers in the codebase are written in Russian/Ukrainian — this is existing convention, not an error.
- `Groovra.Music.Microservice/Model/` (singular) holds Music's entities while `Groovra.Auth.Microservice/Models/` (plural) holds Auth's — inconsistent naming between services, not a typo to "fix" opportunistically.
- Role checks use `Groovra.Shared.Constants.AppRoles` (`Admin`, `Artist`, `Listener`) consistently — reuse these constants rather than hardcoding role strings.
- `appsettings.Development.json` and `.env` are gitignored (contain local secrets/connection strings); `docker-compose.yml` sources secrets from environment variables (`DB_CONNECTION_STRING`, `JWT_SECRET_KEY`, `GOOGLE_CLIENT_ID`/`SECRET`, mail creds).
