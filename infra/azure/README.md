# Azure AI infrastructure (Terraform)

This module creates the Azure AI resources for the clause extraction capability:

- resource group `rg-convenciones-ai` (East US by default);
- Azure AI Document Intelligence (`FormRecognizer`, S0);
- Azure AI Foundry (AI Services) and one model deployment;
- the runtime service principal role assignments (`Cognitive Services User` on both);
- a monthly budget with optional alerts;
- network restriction to the droplet IP (`restrict_network = true` by default).

The application keeps running on the existing droplet and Supabase database. Nothing here moves
the hosting or the database to Azure.

## Where Terraform runs

**Only in GitHub Actions** (`.github/workflows/infrastructure.yml`):

- `pull_request` that touches `infra/azure/**` → `fmt`, `init`, `validate`, `plan` (artifact).
- `push` to `main` (merge) → same checks, then `apply` of the reviewed plan.

Do **not** run `terraform plan` or `terraform apply` from a workstation. The state backend uses
Entra OIDC in CI; local runs would need credentials and could diverge from the pipeline.

## One-time prerequisites (manual, before the first PR)

These are outside Terraform because they bootstrap identity and the state backend.

### 1. Deployer app registration (for GitHub Actions)

Create an Entra app registration (for example `convenciones-ai-deployer`) with:

- a **federated credential** for GitHub Actions:
  - issuer `https://token.actions.githubusercontent.com`
  - subject `repo:acamposve/convencion-aspire-ai:ref:refs/heads/main`
  - audience `api://AzureADTokenExchange`
- a second federated credential with subject `repo:acamposve/convencion-aspire-ai:pull_request`
  so the `plan` job can authenticate on pull requests.

Grant it, at subscription scope:

- `Contributor` (create the resource group and resources);
- `User Access Administrator` (create the role assignments in `identity.tf`);
- `Application Administrator` in Entra **only if** you keep `create_runtime_app = true`
  (Terraform creates the runtime app registration). Otherwise set it to `false` and pass an
  existing service principal object id as `runtime_app_object_id`.

Do not create a client secret for this app: GitHub authenticates with OIDC.

### 2. State storage (one time)

Create the state resource group and storage account once (any authenticated account with
Contributor, for example the portal or `az`):

```bash
az group create -n rg-convenciones-tfstate -l eastus
az storage account create -n <globally-unique-name> -g rg-convenciones-tfstate \
  --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 \
  --allow-blob-public-access false --https-only true
az storage container create --account-name <globally-unique-name> \
  -n tfstate --auth-mode login
```

Then grant the deployer app **Storage Blob Data Contributor** on that storage account (Terraform
uses `use_azuread_auth = true`; no account keys).

### 3. GitHub repository variables

| Variable | Example | Required |
|---|---|---|
| `AZURE_CLIENT_ID` | deployer app client id | yes |
| `AZURE_TENANT_ID` | tenant id | yes |
| `AZURE_SUBSCRIPTION_ID` | subscription id | yes |
| `TF_STATE_RG` | `rg-convenciones-tfstate` | yes |
| `TF_STATE_SA` | `<globally-unique-name>` | yes |
| `TF_STATE_CONTAINER` | `tfstate` | yes |
| `TF_MODEL_NAME` | `gpt-5.4-mini` | yes |
| `TF_MODEL_VERSION` | `2026-03-17` | yes |
| `TF_MODEL_SKU` | `DataZoneStandard` | yes |
| `TF_MODEL_CAPACITY` | `10` | yes |
| `TF_BUDGET_ALERT_EMAIL` | your email | optional |

Find a valid model and version in the region before setting them:

```bash
az cognitiveservices model list --location eastus \
  --query "[?model.name=='gpt-5.4-mini'].{name:model.name, version:model.version, skus:model.skus[].name}" -o table
```

Model availability and quota are not guaranteed by the module: if the region lacks the model or the
TPM quota, the `plan`/`apply` fails and the values must be adjusted (or a quota request raised).

### Current staging deployment (temporary)

Azure OpenAI deployment creation is currently blocked for this subscription with `715-123420`
("unusual activity"). Until support lifts it, the staging deployment uses `Phi-4-mini-instruct`
(format `Microsoft`, SKU `GlobalStandard`, capacity 1) so the rest of the platform can be validated.
Restore the OpenAI model (`gpt-5.4-mini`, `DataZoneStandard`) once the block is removed.

## Flow

1. Open a PR that changes `infra/azure/**`. CI runs `plan` and uploads the plan artifact.
2. Review the plan in the PR's checks.
3. Merge to `main`. CI runs `plan` again and applies that plan automatically.
4. Read the outputs (`document_intelligence_endpoint`, `foundry_endpoint`,
   `model_deployment_name`, `runtime_client_id`, `tenant_id`) from the workflow log and configure the
   application's GitHub environment and the droplet `.env` (secrets are never in Terraform state:
   create the runtime client secret outside Terraform with `az ad app credential reset`).

## What this module does not do

- It does not create the runtime client secret (by design: it must not enter the state).
- It does not create the Supabase database, the droplet or the application containers.
- It does not delete the previous `rg-comparador-demo` resources. That is a separate, destructive
  decision that requires explicit approval and is not automated here.
