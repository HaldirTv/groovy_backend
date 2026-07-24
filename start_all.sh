#!/bin/bash
# Запускает Redis/RabbitMQ (docker) + все 4 .NET сервиса через профиль "https"
# из их собственных launchSettings.json (тот же профиль, что использует Rider) —
# без ручного форсирования портов.

if [ -f .env ]; then
  set -a
  source .env
  set +a
fi

echo "Starting infrastructure containers (Redis, RabbitMQ)..."
docker compose up -d redis rabbitmq || true

pkill -9 -f "Groovra.Auth.Microservice" || true
pkill -9 -f "Groovra.Music.Microservice" || true
pkill -9 -f "Groovra.History.Microservice" || true
pkill -9 -f "Groovra.ChatService.Microservice" || true
pkill -9 -f "Groovra.ApiGateway" || true
sleep 1

export ASPNETCORE_ENVIRONMENT=Development

echo "Building..."
dotnet build Groovra.sln || exit 1

echo "Starting Groovra Auth..."
(cd Groovra.Auth.Microservice && dotnet run --no-build --launch-profile https < /dev/null > ../auth.log 2>&1) &

echo "Starting Groovra Music..."
(cd Groovra.Music.Microservice && dotnet run --no-build --launch-profile https < /dev/null > ../music.log 2>&1) &

echo "Starting Groovra History..."
(cd Groovra.History.Microservice && dotnet run --no-build --launch-profile https < /dev/null > ../history.log 2>&1) &

echo "Starting Groovra Chat..."
(cd Groovra.ChatService.Microservice && dotnet run --no-build --launch-profile https < /dev/null > ../chat.log 2>&1) &

sleep 3

echo "Starting Groovra ApiGateway..."
(cd Groovra.ApiGateway && dotnet run --no-build --launch-profile https < /dev/null > ../gateway.log 2>&1) &

echo "All backend services started. Logs: auth.log, music.log, history.log, chat.log, gateway.log"
wait
