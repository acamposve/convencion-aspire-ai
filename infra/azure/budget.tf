resource "azurerm_consumption_budget_resource_group" "ai" {
  count = (var.budget_alert_email == null || var.budget_alert_email == "") ? 0 : 1

  name              = "budget-${var.prefix}-ai"
  resource_group_id = azurerm_resource_group.ai.id
  amount            = var.budget_amount_usd
  time_grain        = "Monthly"

  time_period {
    start_date = formatdate("YYYY-MM-01'T'00:00:00Z", plantimestamp())
  }

  dynamic "notification" {
    for_each = [50, 80, 100]

    content {
      enabled        = true
      operator       = "GreaterThan"
      threshold      = notification.value
      threshold_type = "Percentage"
      contact_emails = [var.budget_alert_email]
    }
  }
}
