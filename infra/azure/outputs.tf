output "resource_group_name" {
  value = azurerm_resource_group.ai.name
}

output "document_intelligence_endpoint" {
  description = "AzureAi__DocumentIntelligenceEndpoint for the application."
  value       = azurerm_cognitive_account.di.endpoint
}

output "foundry_endpoint" {
  description = "AzureAi__Endpoint for the application."
  value       = azurerm_cognitive_account.foundry.endpoint
}

output "model_deployment_name" {
  description = "AzureAi__Deployment for the application."
  value       = azurerm_cognitive_deployment.clauses.name
}

output "runtime_client_id" {
  description = "AzureAi__ClientId for the application (null when an existing app was supplied)."
  value       = var.create_runtime_app ? azuread_application.runtime[0].client_id : null
}

output "tenant_id" {
  description = "Entra tenant id for the application."
  value       = data.azurerm_client_config.current.tenant_id
}
