# Azure AI resources for Convenciones.
# Apply is performed only by .github/workflows/infrastructure.yml on merge to main.

resource "random_string" "suffix" {
  length  = 6
  lower   = true
  upper   = false
  numeric = true
  special = false
}

resource "azurerm_resource_group" "ai" {
  name     = "rg-${var.prefix}-ai"
  location = var.location
  tags     = var.tags
}

# Document Intelligence: structure extraction (text, paragraphs, roles, tables, bboxes).
resource "azurerm_cognitive_account" "di" {
  name                = "di-${var.prefix}-${random_string.suffix.result}"
  location            = azurerm_resource_group.ai.location
  resource_group_name = azurerm_resource_group.ai.name
  kind                = "FormRecognizer"
  sku_name            = "S0"

  custom_subdomain_name         = "di-${var.prefix}-${random_string.suffix.result}"
  public_network_access_enabled = true

  dynamic "network_acls" {
    for_each = var.restrict_network ? [1] : []
    content {
      default_action = "Deny"
      ip_rules       = [var.droplet_ip]
    }
  }

  tags = var.tags
}

# Azure AI Foundry: hosts the language model deployment.
resource "azurerm_cognitive_account" "foundry" {
  name                = "ai-${var.prefix}-${random_string.suffix.result}"
  location            = azurerm_resource_group.ai.location
  resource_group_name = azurerm_resource_group.ai.name
  kind                = "AIServices"
  sku_name            = "S0"

  custom_subdomain_name         = "ai-${var.prefix}-${random_string.suffix.result}"
  public_network_access_enabled = true

  dynamic "network_acls" {
    for_each = var.restrict_network ? [1] : []
    content {
      default_action = "Deny"
      ip_rules       = [var.droplet_ip]
    }
  }

  tags = var.tags
}

# Model deployment used for clause extraction and classification.
resource "azurerm_cognitive_deployment" "clauses" {
  name                 = "clauses"
  cognitive_account_id = azurerm_cognitive_account.foundry.id

  model {
    format  = var.model_format
    name    = var.model_name
    version = var.model_version
  }

  sku {
    name     = var.model_sku
    capacity = var.model_capacity
  }
}

# The staging deployment was created manually while Azure OpenAI was blocked
# (715-123420). This import adopts it instead of recreating it; it becomes a
# no-op once the resource is in the state.
import {
  to = azurerm_cognitive_deployment.clauses
  id = "/subscriptions/${data.azurerm_client_config.current.subscription_id}/resourceGroups/${azurerm_resource_group.ai.name}/providers/Microsoft.CognitiveServices/accounts/${azurerm_cognitive_account.foundry.name}/deployments/clauses"
}