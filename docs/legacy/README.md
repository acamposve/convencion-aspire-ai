# Legacy reference tree — archived description

This is the **original root `README.md`**, preserved verbatim in substance and relocated here
when the root README was rewritten as the new project README (Task 1).

Changes from the original, recorded for provenance:

- **Encoding repaired.** The committed original was UTF-8 bytes being read as Latin-1, so it
  rendered as mojibake (`Sistema â€” Referencia histÃ³rica`). The text is unchanged; only its
  encoding is correct.
- **Corrections noted inline** where the original disagreed with the tree. The original is
  *not* silently edited — discrepancies are marked `[corrected]`.
- **Superseded governance references.** The original cited `constitucion.md` Art. IX / Art. XI.2.
  No such file existed in the repository. The current authority is
  [../constitution.md](../constitution.md), which preserves both article anchors.

Content below is the original, in Spanish as written.

---

## Legacy Sistema — Referencia histórica

Este directorio contiene el SaaS PHP original (~20 años, ~100 libras de código).

**Propósito:** Referencia funcional y arquetipo de datos — NO se porta código de acá.

**Según constitución.md Art. IX:**
- Legible solo para entender lo que el sistema hacía
- Datos históricos (dump SQL, PDFs reales) pueden usarse como dataset de prueba/validación
- Derechos de reutilización pendiente confirmación (Art. XI.2)
- Estructura de carpetas y módulos adaptadas al nuevo sistema, pero NO se copian métodos/funciones

**Contenido:**
- `index.html` — Portal web histórico
- `admin/` — Interfaz de administración (deprecated)
- `lib/` — Librerías PHP (NO reutilizar directamente)
- `db/` — Scripts SQL legados (datos de referencia) **[corrected: this directory does not exist.
  The legacy dump is the single file `presenci_cccol.sql` at the repository root.]**
- `Scripts/`, `SpryAssets/`, `css/` — Frontend antiguo (reemplazado por React)
- `local/config*.php` — Configuración histórica (ignorar) **[corrected: excluded from version
  control via `.gitignore` — it contains live database credentials.]**
- `xyiznwsk/` — Carpeta sin identificar (posible basura) **[corrected: empty directory; git does
  not track empty directories, so it is absent from the repository.]**

**Cómo usar este código:**
1. ✅ Ver cómo se estructuraban las tablas y relaciones
2. ✅ Extraer SQL de esquema legado para validar migración
3. ✅ Usar datos de prueba (empresas, convenciones clasificadas)
4. ❌ NO copiar funciones PHP
5. ❌ NO reutilizar lógica de negocio directamente
6. ❌ NO importar estilos o scripts JS

Si necesitas información sobre cómo algo funcionaba en el legado, revisa acá — pero reescribelo
en la arquitectura nueva (Art. V: .NET, Python, React).

---

**Archivos legados marcados en git con `@legacy` tag si es necesario referenciarlos desde
la arquitectura nueva.**

---

> **Note (current governance).** Two statements above are no longer accurate and are superseded
> by [../constitution.md](../constitution.md): legacy data is **not** migrated (Art. V.2), and
> legacy PDFs/documents are **not committed** (Art. XI.2). The legacy code remains readable for
> functional understanding only (Art. IX).
>
> Also note point 2 above ("validar migración") and Art. V naming `.NET, Python, React`: the
> mandated stack is C#/.NET + React and **Python is not part of it** (constitution Art. III).
