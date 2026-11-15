# Instrucciones para revisores — anotación de cláusulas

Este documento explica cómo completar la planilla de revisión **sin conocimientos técnicos**.
Solo debe usar Excel (o LibreOffice Calc) y guardar el archivo.

Su planilla: `review-A-template.xlsx` o `review-B-template.xlsx` (según el revisor que sea).

---

## 1. Antes de empezar

- **No abra la planilla del otro revisor.** Cada uno revisa por separado: el valor de esta ronda es
  comparar dos criterios independientes.
- **No use un asistente de inteligencia artificial** para decidir las categorías. La revisión debe ser
  suya.
- **No modifique las columnas A, B, C, D, E ni H** (Clause ID, Documento, Tipo, Número, Encabezado y
  Texto). Son de referencia. Si las toca, la planilla no se podrá procesar.
- **No borre filas ni cambie los encabezados.**

## 2. Lo primero: identificarse

1. Abra el archivo.
2. Vaya a la celda **B3**, al lado de *Reviewer identity (obligatorio)*.
3. Escriba su nombre o un identificador suyo (por ejemplo: `mgarcia` o `Revisor A`).
   Sin este dato la planilla se rechaza.

La celda **B4** (*Submission version*) ya trae `1`. Déjela así.

## 3. Revisar cada cláusula

Cada fila, desde la **fila 8**, es una cláusula. Las columnas con **fondo amarillo** son las que usted
completa: **F, G, I, J, K y L**.

| Columna | Qué escribir |
|---|---|
| **F — Página desde** | Número de la página donde empieza la cláusula en el PDF |
| **G — Página hasta** | Número de la página donde termina |
| **I — Categorías** | Una o varias claves de la hoja *Taxonomia*, separadas por comas |
| **J — Categoría principal** | Una sola clave, que debe estar también en la columna I |
| **K — ¿Ambigua?** | `Sí` o `No` |
| **L — Notas** | Texto libre: por qué eligió, dudas, referencias |

### Sobre las páginas (F y G)

La planilla ya trae una propuesta de páginas tomada de la anotación anterior. **Compruébela contra el
PDF** que está leyendo y corríjala si no coincide: abra el PDF, busque la cláusula y escriba la página
real de inicio y de fin. Si la cláusula ocupa una sola página, escriba el mismo número en F y G.

Escriba solo números (por ejemplo `1` y `2`). Si deja una celda vacía o escribe algo que no sea un
número, la planilla se rechaza y se le indicará la fila.

### Cómo escribir varias categorías

En la columna I escriba las claves separadas por **coma**, por ejemplo:

```
confidentiality, data_protection
```

No agregue explicaciones en esa celda: solo las claves. Las explicaciones van en L.

### La categoría principal

En la columna J escriba **una** de las claves que ya puso en I. Es la categoría que mejor representa la
cláusula. Si la cláusula tiene una sola categoría, repita esa misma clave en J.

Si escribe en J una categoría que no está en I, la planilla se rechaza.

### Si ninguna categoría aplica

Deje **I y J vacías**. Eso es una respuesta válida y esperada: algunas cláusulas no encajan en ninguna
categoría de la taxonomía actual. Explique brevemente en L por qué no aplica ninguna.

No invente claves nuevas. Solo se aceptan las 14 claves de la hoja *Taxonomia*.

### Cuándo marcar "Sí" en ambigua

Marque `Sí` cuando usted considere que **otro revisor cuidadoso podría elegir distinto**. En L explique
la alternativa que consideró. Esto no es un error: es información valiosa para la revisión final.

Si lo tiene claro, escriba `No`.

## 4. Ejemplo de una fila completa

Suponga una cláusula que ocupa las páginas 2 y 3, trata sobre el precio y además impone reserva sobre la
información de pago:

| F | G | I — Categorías | J — Principal | K — Ambigua | L — Notas |
|---|---|---|---|---|---|
| `2` | `3` | `pricing, confidentiality` | `pricing` | `No` | El precio es el objeto principal; la reserva es accesoria. |

## 5. Consultar la taxonomía

La hoja **Taxonomia** del mismo archivo tiene las 14 claves con su nombre y descripción:

| Clave | Nombre | Descripción |
|---|---|---|
| `parties` | Partes y definiciones | Partes contratantes, definiciones y ámbito del convenio |
| `scope` | Alcance y obligaciones | Objeto, alcance y obligaciones de las partes |
| `pricing` | Precio y pago | Salarios, aumentos, pagos y condiciones económicas |
| `duration` | Duración y renovación | Vigencia, prórroga y renovación |
| `termination` | Terminación | Causas y efectos de la terminación |
| `confidentiality` | Confidencialidad | Reserva y manejo de información |
| `data_protection` | Protección de datos | Tratamiento de datos personales |
| `ip` | Propiedad intelectual | Titularidad y uso de propiedad intelectual |
| `liability` | Responsabilidad e indemnización | Responsabilidad, daños e indemnización |
| `warranties` | Garantías | Garantías y declaraciones |
| `disputes` | Resolución de disputas | Procedimientos de solución de controversias |
| `governing_law` | Ley aplicable | Ley y jurisdicción aplicables |
| `force_majeure` | Fuerza mayor | Eventos de fuerza mayor y sus efectos |
| `other` | Otros / Sin clasificar | Cláusulas sin categoría identificable |

Use `other` solo cuando la cláusula sí tiene contenido identificable pero ninguna categoría encaja;
si el contenido no encaja en la taxonomía por una razón de fondo, es preferible dejar I vacía y
explicarlo en L.

## 6. Guardar y entregar

1. Guarde con **Ctrl+S**.
2. **Conserve el formato .xlsx.** Si su programa pregunta, elija "Libro de Excel (.xlsx)".
3. Puede cambiar el nombre del archivo, por ejemplo `review-A-mgarcia.xlsx`. No cambie la estructura.
4. Envíe el archivo por el canal acordado.

No es necesario que usted convierta nada: quien coordina la revisión ejecuta la conversión.

## 7. Errores frecuentes y qué significan

| Mensaje | Qué hacer |
|---|---|
| *Escriba su nombre o identificador en la celda B3* | Complete B3 |
| *Categoría desconocida '...'* | Revise la clave en la hoja Taxonomia; suele ser un error de tipeo |
| *'...' no está entre las categorías seleccionadas* | La columna J debe repetir alguna clave de la I |
| *Valor no válido ... Escriba un número de página* | Corrija F o G |
| *La página final no puede ser menor que la inicial* | Revise F y G |
| *Falta la cláusula '...'* | Se borró una fila: restaure la plantilla original y vuelva a empezar |
| *Cláusula duplicada* | Hay dos filas con el mismo Clause ID |
| *Valor no válido ... Escriba Sí o No* | Corrija la columna K |

## 8. Qué ocurre después

La planilla se convierte al formato que ya usa el proyecto, se valida y luego se compara con la del
otro revisor. Las diferencias **no se resuelven automáticamente**: las decide una persona y queda
registrada la justificación. Su anotación original no se borra en ese proceso.

Con esas decisiones se publica la versión 2 del corpus, que es la que se usa para medir calidad.
