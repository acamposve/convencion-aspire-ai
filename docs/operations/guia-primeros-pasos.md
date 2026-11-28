# Guía de primeros pasos — cómo probar la aplicación en staging

**Para quién es esta guía:** la persona del cliente que va a probar la aplicación en el entorno de
staging (`https://138-197-15-40.sslip.io`).

**Estado del entorno:** staging está desplegado con la versión más reciente, **con el worker de
procesamiento desactivado** y con **datos de demostración**. No es producción y no admite contratos
reales (ver «Antes de empezar»).

---

## Antes de empezar: qué se puede subir

- **Permitido:** contratos **sintéticos**, normativa **pública** (gacetas, leyes, reglamentos) y material
  de demostración anonimizado.
- **Prohibido:** convenios firmados, anexos con datos personales y cualquier documento confidencial de un
  cliente. La política de datos sintéticos sigue vigente hasta que se firmen los términos de los
  proveedores de IA ([#47](https://github.com/acamposve/convencion-aspire-ai/issues/47),
  [#52](https://github.com/acamposve/convencion-aspire-ai/issues/52)).
- El contenido de staging es **desechable**: se puede borrar sin aviso.

Tu cuenta será una cuenta **administradora de tu empresa**: puedes cargar y borrar contratos, crear
acuerdos y comparar. No tienes acceso al código ni a la configuración del servidor.

---

## Paso 1 — Registrar una ley

**Hoy este paso se hace por soporte, no desde la interfaz.** La aplicación permite **añadir artículos**
a una ley existente, pero todavía no tiene un botón «Nueva ley».

1. Pide al equipo que registre la ley (nombre, tipo —ley laboral u otra norma—, fecha de publicación).
   Te la dejamos disponible en **Biblioteca legal** (`/legal`).
2. Entra en **Biblioteca legal → Abrir** la ley.
3. Pulsa **«Editar artículo N»** (o crea uno nuevo) para cargar sus artículos: número, título, resumen,
   cuerpo y, opcionalmente, la **Materia**. Sin Materia, el artículo no aparece en la comparación.

> Solo **una ley laboral** es la que se usa como referencia en la comparación; el resto son normas de
> consulta.

## Paso 2 — Subir un contrato

1. Ve a **Documentos** (`/documents`).
2. Arrastra el PDF a la zona **«Suba un contrato colectivo»** (o pulsa para seleccionarlo). Máximo
   **20 MiB**, solo PDF.
3. Verás el aviso «Documento … subido. Pulse Procesar para extraer el texto».

## Paso 3 — Procesar el documento

1. En la tarjeta del documento pulsa **«Procesar»**: extrae el texto del PDF (queda en `Procesado`).
2. Pulsa **«Procesar cláusulas»**: la IA lee el contrato, separa las cláusulas y las clasifica por
   Materia.
3. **Importante:** en staging el worker está **apagado**. Dentro del diálogo verás el estado **`Queued`**
   (el servidor lo devuelve así, sin traducir), una barra de progreso y el aviso «El procesamiento
   continúa en segundo plano; puede cerrar y volver más tarde». **En staging ese aviso no se cumple**: no
   hay ningún proceso detrás que vaya a avanzar el trabajo, así que el documento se queda en `Queued` y
   sin cláusulas indefinidamente. Es lo esperado y protege el gasto en IA. En la lista, la tarjeta del
   documento sigue mostrando **`Completed`**, porque el texto sí se extrajo: el `Queued` solo se ve dentro
   del diálogo.
4. **No vuelvas a pulsar «Procesar cláusulas» para «ver si arranca».** No es peligroso —la aplicación no
   crea un segundo trabajo ni gasta tokens— pero deja un trabajo encolado vivo, y mientras ese trabajo
   esté encolado la aplicación **no acepta procesar cláusulas de ningún otro documento** de tu empresa:
   responde con un error de límite de ejecuciones simultáneas (409 `concurrency_limit`) hasta que se
   reactive el worker. Un solo documento encolado bloquea el resto.
5. **«Ver texto»** muestra el texto extraído del contrato.

## Paso 4 — Comparar

La comparación trabaja sobre **acuerdos publicados**, no directamente sobre los PDF subidos.

1. Ve a **Acuerdos** (`/agreements`) y pulsa **«Nuevo acuerdo»**.
2. En el editor, añade las cláusulas: número, título, resumen, cuerpo y **Materia** (obligatoria).
3. Pulsa **«Publicar»** y confirma. Un acuerdo en borrador **no** aparece en la comparación.
4. Ve a **Comparación** (`/comparison`):
   - elige la **Materia** en el desplegable;
   - marca los acuerdos publicados que quieras comparar;
   - pulsa **«Comparar»**.
5. Verás las cláusulas de esa materia, una junto a otra, sin afirmar que sean equivalentes, y los
   artículos legales relacionados cuando la ley los tenga cargados y clasificados.

---

## Lo que todavía no se puede hacer (y no es un fallo)

| Limitación | Por qué | Alternativa hoy |
|---|---|---|
| Crear una ley desde la interfaz | Falta el alta de leyes en la UI (la API ya lo soporta) | La registra soporte y tú cargas sus artículos |
| Subir un contrato y compararlo directamente | No existe todavía la promoción de cláusulas extraídas a un acuerdo comparable | Crear el acuerdo y sus cláusulas en el editor (paso 4) |
| Que la IA procese solo tras subir | El worker está apagado en staging | Se activa en una ventana controlada, o se prueba con un acuerdo ya autorado |

Estas dos primeras están en el rediseño pendiente
([#138](https://github.com/acamposve/convencion-aspire-ai/issues/138), #139).

## Si algo no cuadra

Anota **qué pantalla**, **qué botón** y **qué mensaje** apareció. Los mensajes de error de la aplicación
llevan un **código** además del texto: con ese código podemos localizar el problema en el registro del
servidor. Mándanoslo y lo revisamos.

## Glosario mínimo

- **Documento:** el PDF que subes.
- **Acuerdo:** la convención colectiva que se compara; tiene cláusulas y se publica.
- **Materia:** la categoría de la cláusula (por ejemplo, Jornada, Vacaciones, Estructura salarial). Es
  obligatoria para que la cláusula entre en la comparación.
- **Publicado:** el acuerdo es visible en la comparación y en el portal público.
