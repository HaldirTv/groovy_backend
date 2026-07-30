#!/usr/bin/env bash
set -e

RESOURCE_GROUP="rg-groovra-backend"
LOCATION="germanywestcentral"
VM_NAME="vm-groovra-backend"
ADMIN_USER="azureuser"

echo "=== 1. Ensuring Resource Group ==="
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output table

echo "=== 2. Creating Azure VM ==="
az vm create \
  --resource-group "$RESOURCE_GROUP" \
  --name "$VM_NAME" \
  --image Ubuntu2204 \
  --size Standard_B2s \
  --admin-username "$ADMIN_USER" \
  --generate-ssh-keys \
  --public-ip-sku Standard \
  --output table

echo "=== 3. Opening Firewall Ports ==="
az vm open-port --port 80 --resource-group "$RESOURCE_GROUP" --name "$VM_NAME" --priority 100 --output table
az vm open-port --port 5274 --resource-group "$RESOURCE_GROUP" --name "$VM_NAME" --priority 101 --output table

PUBLIC_IP=$(az vm show -d -g "$RESOURCE_GROUP" -n "$VM_NAME" --query publicIps -o tsv)
echo "=== Azure VM Public IP: $PUBLIC_IP ==="
