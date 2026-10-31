variable "location" {
  description = "Azure region for the AI resources. East US is closest to the staging droplet."
  type        = string
  default     = "eastus"
}

variable "prefix" {
  description = "Name prefix for the resources created by this module."
  type        = string
  default     = "convenciones"
}

variable "model_name" {
  description = "Foundry model name to deploy (must exist in the chosen region)."
  type        = string
  default     = "gpt-4.1-mini"
}

variable "model_version" {
  description = "Exact model version from the Foundry catalog (for example 2025-04-14). Required."
  type        = string
  default     = ""

  validation {
    condition     = length(var.model_version) > 0
    error_message = "Set model_version to a version that exists in the Foundry catalog for the chosen region."
  }
}

variable "model_capacity" {
  description = "Provisioned throughput (TPM, in thousands) for the deployment."
  type        = number
  default     = 10

  validation {
    condition     = var.model_capacity > 0
    error_message = "model_capacity must be greater than zero."
  }
}

variable "model_format" {
  description = "Model format: OpenAI for Azure OpenAI models."
  type        = string
  default     = "OpenAI"
}

variable "droplet_ip" {
  description = "Public IPv4 of the staging droplet, allowed when restrict_network is true."
  type        = string
  default     = "138.197.15.40"
}

variable "restrict_network" {
  description = "When true, the AI resources accept traffic only from droplet_ip."
  type        = bool
  default     = true
}

variable "budget_amount_usd" {
  description = "Monthly budget for the resource group, in USD."
  type        = number
  default     = 50
}

variable "budget_alert_email" {
  description = "Email for budget alerts. When null, no notifications are configured."
  type        = string
  default     = null
}

variable "create_runtime_app" {
  description = "Create the runtime app registration and service principal. Set to false when the deployer principal lacks Graph permissions and pass runtime_app_object_id instead."
  type        = bool
  default     = true
}

variable "runtime_app_object_id" {
  description = "Object id of an existing runtime service principal, used when create_runtime_app is false."
  type        = string
  default     = null

  validation {
    condition     = var.create_runtime_app || var.runtime_app_object_id != null
    error_message = "When create_runtime_app is false, runtime_app_object_id is required."
  }
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
  default = {
    project    = "convenciones"
    capability = "ai-clauses"
    managed_by = "terraform"
  }
}
