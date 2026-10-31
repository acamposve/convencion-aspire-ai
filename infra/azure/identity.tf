# Runtime identity used by the droplet. The client secret is created OUTSIDE Terraform
# (`az ad app credential reset`) so it never enters the state file.

resource "azuread_application" "runtime" {
  count = var.create_runtime_app ? 1 : 0

  display_name     = "convenciones-ai-runtime"
  sign_in_audience = "AzureADMyOrg"

  tags = ["convenciones", "ai-clauses"]
}

resource "azuread_service_principal" "runtime" {
  count = var.create_runtime_app ? 1 : 0

  client_id = azuread_application.runtime[0].client_id
}

locals {
  runtime_principal_id = var.create_runtime_app ? azuread_service_principal.runtime[0].object_id : var.runtime_app_object_id
}

# Least privilege: the runtime principal may call the two AI services, nothing else.
resource "azurerm_role_assignment" "di_user" {
  count = local.runtime_principal_id != null ? 1 : 0

  scope                = azurerm_cognitive_account.di.id
  role_definition_name = "Cognitive Services User"
  principal_id         = local.runtime_principal_id
}

resource "azurerm_role_assignment" "foundry_user" {
  count = local.runtime_principal_id != null ? 1 : 0

  scope                = azurerm_cognitive_account.foundry.id
  role_definition_name = "Cognitive Services User"
  principal_id         = local.runtime_principal_id
}
