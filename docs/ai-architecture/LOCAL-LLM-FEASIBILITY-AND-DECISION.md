# LLM local en el Droplet — viabilidad y decisión

**Fecha:** 2026-11-01 · **Estado:** decisión tomada; sin cambios de infraestructura.
**Alcance:** evaluar si Convenciones puede sustituir Azure AI por un LLM local en el Droplet
existente. No se provisionó nada, no se instaló nada en el Droplet y no se modificó Azure.

**Cómo leer:** **VERIFICADO** = leído en el sistema real; **MEDIDO** = observado en el Droplet;
**ESTIMADO** = cálculo con supuestos explícitos; **SUPUESTO** = pendiente de confirmar.

---

## 1. Especificaciones reales del Droplet (VERIFICADO / MEDIDO)

| Dato | Valor |
|---|---|
| Host | `convenciones-staging`, IP `138.197.15.40` |
| SO | Ubuntu 24.04.5 LTS, kernel 6.8.0-147, x86_64 |
| vCPU | **1** (`DO-Regular`, 1 core, 1 thread) |
| RAM | **1.967 MB** totales; 543 MB usados; 1.424 MB disponibles; **swap 0** |
| Disco | 48 GB, 3,1 GB usados, **45 GB libres** |
| GPU | **No** (`Virtio 1.0 GPU`, sin `nvidia-smi`) |
| Docker | 29.9.2; Compose v5.6.1 |
| Contenedores | `convenciones-api-1` (healthy), `convenciones-web-1` |
| Consumo API | ~10,8 % de RAM ≈ **212 MB**; `dockerd` ~96 MB |
| Carga | load average 0.00–0.02 (sin carga de inference) |

La propuesta previa de “1 vCPU / 2 GB” coincide con lo provisionado, pero ahora está **medido**, no
supuesto.

## 2. Inventario Azure AI (VERIFICADO)

| Recurso | Tipo | Estado | Nota |
|---|---|---|---|
| `rg-convenciones-ai` | Resource group | Existe | Contiene solo recursos de IA |
| `di-convenciones-hxetrj` | Document Intelligence `FormRecognizer` S0 | Existe | Endpoint `https://di-convenciones-hxetrj.cognitiveservices.azure.com/` |
| `ai-convenciones-hxetrj` | AI Services `AIServices` S0 | Existe | Endpoint `https://ai-convenciones-hxetrj.cognitiveservices.azure.com/` |
| Deployment `clauses` | Phi-4-mini-instruct v1, GlobalStandard, 1K TPM | `Succeeded` | Temporal |
| Deployment `clauses-mistral` | Mistral-Large-3 v1, DataZoneStandard, 10K TPM | `Succeeded` | Candidato real |
| App registration `convenciones-ai-runtime` | Entra ID | Sin secreto | Rol `Cognitive Services User` en ambos recursos |
| Terraform | `infra/azure/` | Definido y aplicado | No se toca en esta decisión |
| Código de aplicación | **Sin referencias a Azure** | VERIFICADO | `git grep -i azure -- src` no devuelve nada |
| SDKs Azure | **Ninguno** en `Directory.Packages.props` | VERIFICADO | La integración aún no se ha implementado |

**Distinción importante:** el Terraform de estado (`rg-tfstate-convenciones`), el OIDC de GitHub y la
infraestructura compartida **no** son artefactos de IA y no se tocan.

## 3. Candidatos locales (runtime y modelo)

| Runtime | Ventaja | Problema en este Droplet |
|---|---|---|
| **llama.cpp** | Binario pequeño, CPU, cuantización GGUF, gramática JSON | 1 vCPU; sin swap; rendimiento muy bajo |
| **Ollama** | Fácil de operar, API OpenAI-like, descarga de modelos | Consume RAM adicional; mismo límite de CPU |
| vLLM | Alto rendimiento | Requiere GPU para ser práctico; no aplica |
| LocalAI | API compatible | Más piezas; mismo límite físico |

| Modelo (Q4) | Tamaño aprox. | RAM mínima práctica | Contexto | Español legal | JSON |
|---|---:|---:|---|---|---|
| Qwen2.5-1.5B | ~1,0 GB | ~2,0 GB con KV | 32K | Limitado | Aceptable con gramática |
| Llama-3.2-3B | ~2,0 GB | ~3,0 GB | 128K | Limitado | Medio |
| Phi-4-mini 3.8B | ~2,4 GB | ~3,5 GB | 128K | Medio | Medio |
| Mistral-7B / Qwen2.5-7B | ~4,4 GB | ~6,0 GB | 32K | Bueno | Bueno con gramática |
| Mistral Large 3 (675B) | — | cientos de GB | 256K | Muy bueno | Muy bueno | **Imposible** |

## 4. Cálculo de memoria y CPU (ESTIMADO con base medida)

Presupuesto actual: **1.967 MB** totales; **543 MB** ya usados por SO + Docker + API; **1.424 MB**
libres; **sin swap**.

| Componente | Consumo estimado |
|---|---:|
| SO + systemd + Docker | ~350 MB |
| API .NET + web (actual) | ~250 MB |
| Runtime de inferencia (llama.cpp/Ollama) | ~100–300 MB |
| Modelo 3B Q4 (pesos) | ~2.000–2.400 MB |
| KV cache (8K contexto) | ~300–600 MB |
| **Total con 3B** | **~3.000–3.900 MB** |

**No cabe.** Ni siquiera un 1.5B Q4 (~1,2 GB) deja margen para KV cache y picos: quedarían ~100–200 MB
y el OOM killer mataría la API. Un 7B requiere ~6 GB: imposible.

CPU: 1 vCPU.Un 3B Q4 genera ~5–8 tokens/s en CPU; un 7B ~2–3 tokens/s. Un contrato de 30 páginas
(~15K tokens de entrada) tarda **6–15 minutos solo en prefill** y **5–15 minutos** en generar 2–4K
tokens de salida; un documento grande puede superar **30–45 minutos**. Con un solo vCPU, la API y el
worker compiten por el mismo núcleo. No hay concurrencia real.

## 5. Calidad y evaluación

No se ejecutó un PoC con modelos locales porque **ningún modelo útil cabe** en la RAM disponible. Un
PoC con 1.5B–3B mediría latencia, no calidad representativa: para cláusulas legales en español se
necesita al menos 7B–14B, que tampoco caben.

Riesgos de calidad con modelos pequeños:
- Límites de cláusula imprecisos y omisiones en documentos largos.
- JSON inválido o campos inventados; la validación en la aplicación los rechazaría, pero el coste de
  reintentos en CPU es prohibitivo.
- Terminología legal venezolana/latinoamericana y referencias a leyes: alta probabilidad de
  alucinación; el diseño exige evidencia y validación humana.
- Sin GPU, la latencia impide el flujo asíncrono esperado (varios minutos por contrato, no segundos).

## 6. Riesgos operativos

- **OOM:** sin swap, cualquier pico de modelo mata la API o el worker.
- **Actualizaciones:** descargar modelos en cada arranque no es viable; requeriría volumen persistente
  y ~2–5 GB de disco (hay 45 GB, pero no RAM).
- **Actualizaciones de modelo:** descarga y validación manual; sin GPU, cada cambio es costoso.
- **Monitorización:** no hay GPU/telemetría de inferencia; habría que añadir métricas y límites.
- **Seguridad:** el runtime debe quedar en red interna; no es el problema principal frente a la RAM.
- **Coexistencia:** la API ya usa ~212 MB; el sistema queda al límite con 1.4 GB libres.

## 7. Veredicto

## **NOT FEASIBLE** en la infraestructura actual.

No cumple los criterios de “FEASIBLE”:
1. No cabe en la RAM real (1.967 MB, sin swap).
2. No coexiste con la API/web sin riesgo de OOM.
3. No ofrece tiempos aceptables (minutos por contrato, sin concurrencia).
4. La calidad de un modelo que sí cupiera (1.5B–3B) no es aceptable para cláusulas legales.
5. Requeriría una ampliación de Droplet (≥8 GB RAM y ≥4 vCPU, o GPU), **no aprobada**.
6. Operación/monitorización de modelos locale sin GPU es frágil.

**Alternativa descartada:** Droplet de 8 GB/4 vCPU (~$48–56/mes) o GPU (mucho más). No se provisiona.

## 8. Camino seleccionado

**PATH B — mantener Azure AI y comenzar la implementación de los issues existentes.**

No se elimina nada de Azure; no se toca Terraform; no se cambia el Droplet.

## 9. Progreso de implementación

**Issue elegido: #50 — Cliente Azure AI Foundry con salida estructurada.**

Implementado en la rama `feat/ai-foundry-client`:

- Nuevo módulo `src/Convenciones.Ai` con `IAiModelClient`, `AiCompletionRequest/Result`, `AzureAiOptions`,
  excepciones tipadas (`AiConfigurationException`, `AiUnavailableException`, `AiTimeoutException`,
  `AiInvalidResponseException`).
- `AzureFoundryAiModelClient`: POST a `/models/chat/completions`, JSON mode o JSON Schema, validación
  con `JsonSchema.Net`, **un** intento de reparación, reintentos con backoff para 429/5xx y timeout por
  intento.
- `FakeAiModelClient` con respuestas guionizadas para tests sin red.
- `AddAzureAi` para registro DI con validación de configuración al arranque (no se ha conectado aún al
  `Program.cs` para no romper el despliegue actual; se hará en #42).
- 7 tests unitarios nuevos (válido, reparación, doble inválido, 429, timeout, credencial ausente, fake).
  Suite unitaria completa: **71 tests en verde**; `dotnet build Convenciones.slnx` en verde.
- Sin llamadas reales a Azure, sin secretos, sin cambios de infraestructura.

Pendiente de #50: bake-off real (requiere credencial configurada y set dorado) y wiring en la API.

## 10. Acciones manuales restantes

- Ninguna para la decisión.
- Para ejecutar integraciones reales: proporcionar/configurar la clave o el secreto de la app de runtime
  en GitHub `staging` y en el Droplet (nunca en el repositorio).
- Si en el futuro se quisiera LLM local: aprobar y provisionar un Droplet mayor; hoy **no**.

## 11. Costes e implicaciones

- **Actual:** Azure AI se factura por uso (Mistral ~$0,61/$1,82 por 1M; sin cargos fijos relevantes) +
  Droplet $12/mes + Supabase Free.
- **LLM local:** $0 de tokens, pero requiere Droplet de ≥8 GB/4 vCPU (~$48–56/mes) o GPU; el ahorro de
  tokens no compensa el coste y la latencia.
- **No se incurre en ningún coste nuevo con esta decisión.**

## 12. Evidencia

- SSH al Droplet: `nproc` = 1; `free -m` = 1.967 MB total, 543 usados, 1.424 disponibles, swap 0;
  `df -h` = 45 GB libres; `lspci` sin GPU; `docker ps` = api + web.
- `az cognitiveservices account list` y `deployment list`: DI, Foundry, `clauses` (Phi),
  `clauses-mistral` (Mistral-Large-3).
- `git grep -i azure -- src`: sin resultados.
- `Directory.Packages.props`: sin SDKs Azure.
- Cálculos de memoria/CPU: estimaciones basadas en tamaños Q4 y rendimiento CPU documentado de
  llama.cpp; **no** son mediciones en este Droplet porque ningún modelo útil cabe.
