#!/bin/bash

if [ -f .env ]; then
  set -a
  source .env
  set +a
fi

echo "Starting infrastructure containers (MSSQL, Redis, RabbitMQ)..."
docker compose up -d mssql redis rabbitmq || podman compose up -d mssql redis rabbitmq || true

pkill -9 -f "Groovra" || true
pkill -9 -f "vite" || true
sleep 1

export ASPNETCORE_ENVIRONMENT=Development

echo "Starting Groovra Auth..."
(cd Groovra.Auth.Microservice && dotnet bin/Debug/net10.0/Groovra.Auth.Microservice.dll --urls "http://localhost:5159;https://localhost:7008" < /dev/null > ../auth.log 2>&1) &

echo "Starting Groovra Music..."
(cd Groovra.Music.Microservice && dotnet bin/Debug/net10.0/Groovra.Music.Microservice.dll --urls "http://localhost:5172;https://localhost:7176" < /dev/null > ../music.log 2>&1) &

echo "Starting Groovra Billing..."
(cd Groovra.Billing.Microservice && dotnet bin/Debug/net10.0/Groovra.Billing.Microservice.dll --urls "http://localhost:5041;https://localhost:7188" < /dev/null > ../billing.log 2>&1) &

echo "Starting Groovra Chat..."
(cd Groovra.ChatService.Microservice && dotnet bin/Debug/net10.0/Groovra.ChatService.Microservice.dll --urls "http://localhost:5288;https://localhost:7288" < /dev/null > ../chat.log 2>&1) &

echo "Starting Groovra ApiGateway..."
(cd Groovra.ApiGateway && dotnet bin/Debug/net10.0/Groovra.ApiGateway.dll --urls "http://localhost:5274;https://localhost:7005" < /dev/null > ../gateway.log 2>&1) &

echo "Starting Groovra History..."
(cd Groovra.History.Microservice && dotnet bin/Debug/net10.0/Groovra.History.Microservice.dll --urls "http://localhost:5204;https://localhost:7232" < /dev/null > ../history.log 2>&1) &

echo "Starting Frontend..."
(cd groovy_frontend && npm run dev < /dev/null > frontend.log 2>&1) &

echo "All services started! Holding active..."
wait
