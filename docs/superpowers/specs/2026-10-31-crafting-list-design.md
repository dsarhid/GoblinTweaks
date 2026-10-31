# Crafting Materials: lista de crafteo con total de materiales

Fecha: 2026-10-31 · Tweak: `CraftingMaterials` · Ventana: `UI/CraftingMaterialsAddon`

## Intención

El usuario quiere completar el crafting log con varios ítems que todavía no crafteó. Hoy tiene que abrir receta
por receta para ver qué materiales pide cada una. Quiere armar una especie de presupuesto: marcar varias recetas,
decir cuántas veces va a craftear cada una y ver en un solo lugar la suma de todos los ingredientes (con sus
subingredientes desplegables), cuánto le falta y cuánto cuesta lo que le falta.

Sale bien si, con 5 o 10 recetas marcadas, en una sola pantalla sabe qué tiene que juntar o comprar para hacer
todo, sin abrir cada receta.

## Objetivos

- Marcar recetas desde la lista del medio, en cualquier vista, con una casilla en cada fila.
- Una vista nueva en la barra izquierda, **Mi lista (N)**, donde:
  - la columna del medio muestra solo las recetas marcadas, cada una con un contador de cantidad, más un botón
    **Vaciar lista**;
  - el panel derecho muestra el **total de materiales** de todas las recetas × su cantidad, como árbol desplegable,
    con una casilla **Descontar lo que ya tengo** arriba, una columna de precio estimado y el total en gil.
- La lista (recetas y cantidades) y el estado de la casilla de descontar se guardan entre sesiones.
- Siguen funcionando los extras de hoy: íconos, tooltip del ítem, tooltip de dónde está cada material, clic derecho,
  precios de Universalis, y el resaltado de los materiales listados en el cofre de la FC, las bolsas, los retainers,
  la saddlebag y las tiendas.
- Todo texto nuevo en inglés y español (`CraftLoc`).

## No objetivos

- No se cambia cómo se ve ni cómo funciona el panel de detalle de una receta fuera de la vista Mi lista
  (árbol de ingredientes y botón de lista de compras de una receta quedan como están).
- No hay varias listas con nombre, ni una lista por personaje: es una sola lista para toda la cuenta.
- No se automatiza nada (ni compras en el market board, ni crafteo, ni mover ítems).
- No se calcula si conviene más comprar un intermedio que craftearlo.

## Anti-objetivos (fallaría aunque "funcione")

- Que la ventana crashee o deje nodos colgados al cerrarse (ver reglas de KamiToolKit abajo).
- Que una receta agregada desaparezca de Mi lista porque un filtro de arriba la oculta.
- Que el total de gil cuente dos veces el mismo material (un intermedio y, además, lo que lleva adentro).
- Que la lista del medio quede apretada o ilegible en las vistas normales.
- Cualquier UI con ImGui: todo es nativo (KamiToolKit).

## Lo que decidió el usuario

- Combinación de dos mockups: se marca desde la lista del medio con casillas; el total vive en una entrada de la
  barra izquierda.
- Contador por receta para elegir cuántas veces se craftea; el total se actualiza al cambiarlo.
- Casilla **Descontar lo que ya tengo** arriba del total, configurable.
- La lista se guarda; hay un botón para vaciarla.
- Tabla del total con columna de **precio estimado**, íconos en cada fila y tooltips.
- Precio estimado de una fila = lo que cuesta comprar lo que falta (faltante × precio unitario); "—" si no falta
  nada. Abajo, una fila con el **total de lo que falta** en gil.

## Decisiones mías (el usuario las vio y estuvo de acuerdo)

- El contador aparece **solo** en la vista Mi lista; en las demás vistas cada fila tiene solo la casilla.
- Marcar la casilla agrega la receta con cantidad 1; desmarcarla (en cualquier vista, Mi lista incluida) la saca.
- Contador entre 1 y 999.
- Mi lista ignora la búsqueda y todos los filtros de la barra de arriba (ocultar crafteados, mi nivel, clase).
- Con la casilla de descontar **apagada**, la columna de precio y el total pasan a ser el costo de todo lo necesario,
  como si no tuvieras nada.
- El total en gil suma **solo materiales base** (las hojas del árbol totalmente desplegado), nunca intermedios.
- Si cambia el inventario, el total se recalcula solo (mismo debounce que hoy).
- Lista vacía: el panel derecho muestra un texto del estilo "Marcá recetas con la casilla para sumarlas acá".

## Cómo se calcula el total

Datos de entrada: la lista guardada `[(RecipeId, Count)]`, el `InventorySnapshot` actual y la casilla de descontar.

**Filas de primer nivel.** Los ingredientes directos de todas las recetas de la lista, agrupados por ítem:
`Necesito = Σ (cantidad del ingrediente en la receta × Count de la receta)`. Un ingrediente que usan dos recetas
aparece una sola vez con la suma.

**Desplegar un intermedio.** Un ingrediente crafteable se despliega igual que hoy en el árbol de una receta
(`IsCraftable`, profundidad máxima `MaxRecipeDepth` = 6, sin seguir ciclos). Sus hijos son los ingredientes de la
receta que lo produce, para la cantidad de crafteos que hacen falta:
- con descontar prendido: `crafteos = ceil(faltante / ResultAmount)`, donde `faltante = max(0, Necesito − Tengo)`;
- con descontar apagado: `crafteos = ceil(Necesito / ResultAmount)`.

Si un intermedio no tiene faltante (descontar prendido y ya lo tenés todo), se puede desplegar igual pero sus
hijos piden 0; el builder puede elegir no mostrar la flecha en ese caso.

**Columna Tengo / Necesito.** Se muestra siempre, con la casilla prendida o apagada. "Tengo" es lo mismo que usa hoy
`IngredientCheck.Available`: bolsas más las fuentes que el usuario incluyó en los ajustes del tweak (saddlebag,
retainers, cofre de la FC). Los colores son los de hoy (verde en bolsas, amarillo en otro inventario, rojo si falta).

**Precio estimado por fila.** `precio unitario × faltante` con descontar prendido, o `× Necesito` con descontar
apagado. "—" si da 0. "…" mientras Universalis está cargando. Vacío si no hay precio. El precio unitario es el que ya
usa la ventana (`UnitPrice`: el promedio o, si no hay, el listado más barato). Los intermedios también muestran su
precio (lo que costaría comprarlos en vez de craftearlos), pero solo como dato.

**Total de lo que falta (gil).** Se calcula con el mismo algoritmo que `CraftingMaterials.ShoppingList`, extendido a
varias raíces con cantidades: primero usa lo que tenés (intermedios incluidos), respeta `ResultAmount` y aprovecha lo
que sobra de un subcrafteo para el siguiente. Con descontar apagado, ese algoritmo no toma nada del stock. Se suman
`precio unitario × cantidad a comprar` de los materiales base resultantes. Si algún material base no tiene precio, el
total se muestra como mínimo (igual que hoy `market.costmin`); si alguno está cargando, se muestra cargando.

Nota: el árbol calcula cada nodo con su propio "Tengo", sin repartir el stock entre ramas; el total en gil sí lo
reparte. Si un mismo intermedio aparece en dos ramas, la suma de las filas del árbol puede no coincidir exactamente
con el total. Se acepta: el total es el número correcto, el árbol es para mirar.

## Interfaz

Todo en la ventana existente; nada de ventanas nuevas.

**Barra izquierda.** Una quinta entrada debajo de las cuatro de Estado: "My list (N)" / "Mi lista (N)", con N =
cantidad de recetas en la lista. Se comporta como las otras: al elegirla se deseleccionan las demás y el trabajo.
Sin emoji (la fuente del juego no los tiene). La vista se recuerda como las otras (`Options.View`).

**Lista del medio, vistas normales.** Cada `RecipeRowNode` suma una casilla a la izquierda del ícono. Clic en la
casilla = entra o sale de la lista; **no** selecciona la fila ni cambia el panel derecho. Clic en el resto de la fila
= lo de hoy (detalle de la receta).

**Lista del medio, vista Mi lista.** Las recetas de la lista, en el orden en que se agregaron. Cada fila: casilla
(marcada), ícono, nombre, subtítulo de hoy, y el contador a la derecha. Tooltip del ítem en la fila, clic derecho como
hoy. Debajo de la lista, el botón **Vaciar lista**. Hacer clic en el nombre de una receta en esta vista no reemplaza
el total del panel derecho.

**Panel derecho, vista Mi lista.** De arriba hacia abajo: título "Total de materiales", casilla "Descontar lo que ya
tengo", encabezados de columna (Material · Tengo / Necesito · Precio est.), el árbol, y la fila "Total de lo que
falta: X gil". Cada fila del árbol mantiene lo que ya tiene `IngredientRowNode` (flecha +/−, ícono, nombre con color,
cantidades, ubicación principal, tooltip de ubicaciones o del ítem, clic derecho) y suma la columna de precio. Al
tooltip de ubicaciones se le agrega el precio unitario.

**Lista vacía.** El panel derecho muestra solo el texto de lista vacía; el botón Vaciar lista se oculta o queda
deshabilitado.

**Barra de filtros de arriba en Mi lista** (decisión mía, a partir de la revisión). Los controles quedan visibles
pero no se aplican a Mi lista, que ordena por orden de agregado. Si al volver a otra vista los filtros siguen como
estaban, no se pierde nada.

**Misma receta en dos clases.** La lista se identifica por `RecipeId`, así que dos recetas distintas del mismo ítem
(por ejemplo de dos crafters) son dos entradas separadas.

**Barra de abajo.** "Showing N" en Mi lista cuenta las recetas de la lista.

**Resaltados.** En Mi lista, `ListedItemIds` / `FCChestMarks` / `IngredientItemIds` devuelven los ítems del árbol del
total (las ramas abiertas incluidas), aunque no haya receta seleccionada. Hoy esos métodos devuelven vacío si
`_selectedEntry` es null; eso tiene que cambiar para esta vista. Las tiendas se resaltan con el mismo criterio.

## Persistencia

En `CraftingMaterials.Options`:
- la lista: recetas con su cantidad, en orden (por ejemplo `List<CartItem>` con `RecipeId` y `Count`);
- `bool` de descontar, por defecto `true`.

Se guarda con el mismo mecanismo que los filtros (`Tweak.SaveFilters()`, sin notificación) cada vez que cambia algo.
Al cargar se descartan las recetas que ya no existen en los datos del juego y se acota `Count` a 1–999. Una config
vieja sin estos campos arranca con la lista vacía.

## Restricciones

- UI nativa con KamiToolKit, nunca ImGui. Seguir los patrones de la ventana (`ListNode` + `ListItemNode`, refresco de
  colisiones con `RequestCollisionRebuild()` después de repoblar una lista).
- Memoria del proyecto: un `ListNode` reutiliza los nodos de fila y llama a `SetNodeData` antes de actualizar
  `ItemData`; cualquier estado de una fila (casilla, contador) tiene que salir de los datos, no del nodo. Los
  dropdowns cuyas opciones cambian reciben `Options` solo después de `AttachNode` (si no, crash al cerrar); no hay
  dropdowns nuevos en este diseño, pero vale para cualquier control que se agregue.
- Errores dentro de `Update()` ya se reportan con `Tweak.Fail`; no se agregan hilos ni `Task.Delay`.
- Universalis: pedir precios con `RequestIfNeeded` (prioridad `urgent` para lo que está en pantalla) de todos los
  ítems del árbol y de los materiales base del total.
- Textos en `CraftLoc` (en y es, español en voseo como el resto de la ventana).
- Rendimiento: el total se recalcula al cambiar la lista, un contador, la casilla o el inventario; no cada frame.

## Lo que queda a criterio del builder

- El control del contador (input numérico de KamiToolKit, botones −/+, o los dos), siempre que sea nativo, funcione
  dentro de una fila reciclada y respete 1–999.
- Cómo meter la casilla en `RecipeRowNode` sin que el clic dispare la selección de la fila.
- Si el panel del total reutiliza `_ingredientList` / `IngredientRowNode` con una columna más, o usa una lista y un
  tipo de fila propios. Si se reutiliza, el panel de detalle normal no debe mostrar la columna de precio.
- Ancho de columnas (la de ubicación puede achicarse para hacer lugar al precio).
- Dónde va el código del cálculo (método nuevo en `CraftingMaterials` junto a `ShoppingList`, o refactor de
  `ShoppingList` para aceptar varias raíces y el modo sin descontar).
- Release (changelog, versión): fuera de este trabajo, lo decide el usuario después de probar en el juego.
