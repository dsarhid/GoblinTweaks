# Crafting Materials: totales de My List "Por origen"

Fecha: 2026-11-15 · Tweak: `CraftingMaterials` · Ventana: `UI/CraftingMaterialsAddon` (panel "Total materials" de My List)

## Intención

El panel "Total materials" de My List hoy es un árbol: arriba los ingredientes directos de las recetas de la lista
(sumados por item), y cada intermedio crafteable se abre para mostrar de qué está hecho. El orden es el de las recetas
agregadas. Para salir a buscar materiales eso no sirve:

- los cristales (shards, crystals, clusters) se mezclan con todo y estorban;
- un mismo material base aparece en varios lugares (suelto y dentro de uno o más intermedios) sin sumarse, así que
  vas a buscar 6 y después descubrís que eran 14;
- nada te dice qué cosas se consiguen en el mismo lugar, así que terminás yendo varias veces a la misma zona.

Lo que el usuario quiere: **si tengo que ir a un lugar, ver juntas todas las cosas de la lista que tengo que hacer
ahí, para no repetir viajes**. "Un solo viaje" no es literal: es que lo de una misma zona esté agrupado.

## Objetivos

- Un selector **Árbol / Por origen** en el panel. "Árbol" es la vista actual, sin cambios.
- "Por origen" muestra cada material **una sola vez, con su total**, agrupado por dónde se consigue:
  - **una sección por zona** con todo lo que hay que recolectar ahí (Miner, Botanist, Fisher, Spearfishing juntos);
  - los grupos sin zona: comprar a vendedor, otros (drops, canjes, Market Board), intermedios a craftear antes;
  - **cristales al final**, en su propia sección;
  - lo que ya tenés completo, al final, en su propia sección.
- Datos sacados solo de las hojas del juego (Lumina): sin red, sin servicios externos.

## No objetivos

- Ubicar vendedores por zona (opción B descartada: el juego no lo da de forma simple; solo 183 de 490 vendedores se
  ubican por la hoja `Level`, el resto requiere rastrear menús de NPC y archivos de mapa).
- Drops de monstruos por zona (no están en los datos del juego).
- Horarios de Eorzea de los puntos con horario, rutas, orden óptimo entre zonas, teleports o flags en el mapa.
- Cambiar la vista Árbol, el cálculo de "Deduct what I own" o el total en gil.

## Anti-objetivos

- Que la vista nueva muestre cantidades distintas de las que usa el total en gil: las dos salen del mismo cálculo.
- Que un material aparezca en dos zonas a la vez (volvería el problema de "no se suma").
- Que se rompa con un parche: si una hoja cambia o falta un dato, el material cae en "Otros", nunca un crash.

## Restricciones del proyecto

- UI nativa KamiToolKit, nunca ImGui (`CLAUDE.md`).
- Dropdowns de KTK: las opciones se asignan después de `AttachNode` (memoria "KTK dropdown options crash").
- Textos en inglés y español: `UI/CraftLoc.cs` (la ventana de Crafting Materials usa `CraftLoc`, no `en.json`).
- La lógica sin Dalamud va en `GoblinTweaks/Crafting/` para que `GoblinTweaks.Tests` la compile y pruebe.

## Enfoques considerados

- **A. Recolección por zona con datos del juego (elegido).** Zonas de `GatheringItem`/`GatheringItemPoint`/
  `GatheringPoint`, `FishingSpot` y `SpearfishingItem`; vendedor por `GilShopItem` (sin zona). Confiable y offline.
  Cubre el caso principal: salir a recolectar sin repetir zona.
- **B. A + vendedores por zona.** Descartado: mucho trabajo, incompleto y frágil ante parches (ver No objetivos).
- **C. Datos externos (Garland Tools / Teamcraft).** Descartado: depende de un tercero online y de su formato.

El usuario eligió A, y que se mantenga el árbol con un selector.

## Diseño

### Lo que se ve

Bajo "Deduct what I own", entre la casilla y "Clear list", un selector de dos opciones: **Árbol** / **Por origen**.
La elección se guarda en los filtros del tweak (como `CartDeductOwned`) y se recuerda entre sesiones. Por defecto:
Árbol (los que actualizan no ven un cambio que no pidieron).

Vista "Por origen" (mismas columnas que hoy: material, tengo/necesito, lugar, precio):

```
▾ Yak T'el · Miner                                2 por conseguir
    Cobalt Tungsten Ore         17/2   Xmun Hojaw
    Raw Pink Beryl               0/6   Iq Rrax Tsoly
▾ Elpis · Miner                                   1 por conseguir
    Ambrosial Water              0/1   Northerly Zephyrneus        461
▾ Comprar a vendedor                              1 por conseguir
    ...
▾ Otros (drops, canjes, Market Board)             2 por conseguir
    Ty'aitya Wingblade           0/1   Not found                   821
▾ Craftear antes                                  2 recetas
    Annite Whetstone           ×2 crafts
    Tungsten Ink               ×1 craft
▾ Cristales
    Wind Crystal           10661/72   Main bags +
▾ Ya tenés todo
    Annite                      10/8   FC Chest (1.45)
```

- **Encabezado de grupo**: una fila propia, en dorado, con un caret para plegar/desplegar y a la derecha cuántos
  materiales faltan. Click en el encabezado pliega o despliega el grupo. Los grupos plegados se recuerdan mientras la
  ventana está abierta (no se guardan en la configuración). Por defecto todos abiertos.
- **Filas de material**: el mismo `IngredientRowNode` de hoy, a profundidad 0, sin caret. Colores igual que hoy
  (verde completo, amarillo en otro inventario, rojo falta).
- **Columna de lugar**: si el material falta y no lo tenés en ningún lado (hoy "Not found"), en un grupo de zona
  muestra el **lugar de recolección** (el `PlaceName` del punto, ej. "Xmun Hojaw"); en el resto, lo mismo que hoy.
- **Tooltip** (además de lo de hoy): clase y nivel de recolección, "con horario" si el punto es temporal, y
  "También en: …" con las otras zonas cuando hay más de una. En "Comprar a vendedor", nada extra.
- **Fila de "Craftear antes"**: ícono y nombre del intermedio, y en lugar de tengo/necesito, "×N crafts". Sin precio.
- Click en una fila de material: nada (no hay nada que abrir). Click derecho: el menú de item de siempre.

### Qué entra en cada grupo

Se parte de la lista de **materiales base** que ya calcula `CraftMath.BaseMaterials` (la misma que usa el total en
gil, con o sin "Deduct what I own"). A cada material se le asigna un solo grupo, en este orden de prioridad:

1. **Cristales**: `ItemUICategory` = 59 (Crystal). Va a "Cristales" aunque le falte.
2. **Ya tenés todo**: lo que tenés en total (todos los inventarios) alcanza para lo necesario.
3. **Zona**: se puede recolectar (minería, botánica, pesca o arpón) en al menos una zona.
4. **Comprar a vendedor**: está en algún `GilShopItem`.
5. **Otros**: todo lo demás.

Prioridad recolección antes que vendedor: si igual vas a esa zona, lo juntás ahí (llamada del diseño; el tooltip
dice "También se compra a vendedor").

**Craftear antes**: los intermedios que hay que craftear (los que el cálculo descompone porque no alcanzan), con
cuántos crafts de cada uno, ordenados para craftear de abajo hacia arriba (lo más profundo primero). Hace falta que el
cálculo de materiales base devuelva también los crafts de cada intermedio; se agrega sin cambiar lo que ya devuelve.

### Materiales en varias zonas

Unos 107 de 1111 materiales recolectables están en más de una zona (53 en tres o más). Para que cada uno aparezca en
**una sola** zona y que los viajes se junten:

- repetir: elegir la zona que cubre más materiales aún sin zona (de los que faltan); asignarle todos los que puede;
- empate: la zona con más materiales de una sola zona (ya obligatoria); luego el nombre.

Así un material que se puede sacar en Yak T'el o en Kozama'uka queda en la zona a la que de todos modos tenés que ir.
Las otras zonas van en el tooltip.

### Orden

- Grupos de zona primero, ordenados por cantidad de materiales que faltan (más primero), luego por nombre.
- Después: Comprar a vendedor, Otros, Craftear antes, Cristales, Ya tenés todo.
- Dentro de un grupo: los que faltan primero, luego por nombre.
- Encabezado de zona: "Zona · Clases" (las clases de recolección necesarias en esa zona, ej. "Miner, Botanist").

### Datos de recolección (nuevo, con Dalamud)

Un índice construido una vez desde las hojas, en el idioma del cliente, que para cada item da:

- zonas (`TerritoryType`) con nombre de zona y del lugar (`PlaceName`), y la clase (Miner para Mining/Quarrying,
  Botanist para Logging/Harvesting, Fisher para pesca y arpón);
- nivel de recolección (`GatheringItemLevel`), si es de punto con horario (`GatheringPointTransient` con tabla de
  aparición o ventana efímera), y si se vende en `GilShopItem`.

Fuentes: `GatheringItem` → `GatheringItemPoint` (subfilas) → `GatheringPoint` (`TerritoryType`, `PlaceName`,
`GatheringPointBase.GatheringType`); `FishingSpot` (`Item[]`, `TerritoryType`, `PlaceName`); `SpearfishingItem`
(`Item`, `TerritoryType`). Filas inválidas o sin zona se ignoran.

### Separación del código

- `Crafting/` (sin Dalamud, con tests): el agrupado (asignación a grupos, zonas por cobertura, orden) a partir de
  materiales, cantidades y una descripción de las fuentes de cada item; y la extensión del cálculo con los crafts de
  intermedios.
- `Core/` o el tweak (con Dalamud): el índice de recolección desde Lumina.
- `UI/CraftingMaterialsAddon`: el selector, el armado de filas por grupo y el plegado.
- `UI/Nodes/IngredientRowNode`: soporte de fila de encabezado y de fila de "crafts".

### Textos

En `CraftLoc` (inglés y español, voseo): las dos opciones del selector, los nombres de los grupos, "N por conseguir",
"N recetas", "×N crafts", las líneas del tooltip (clase y nivel, con horario, También en, También se compra a
vendedor) y un párrafo en la ayuda de la ventana que explique las dos vistas.

## Pruebas

- Unitarias (`GoblinTweaks.Tests`): prioridad de grupos, cristales, completos, cobertura de zonas (incluye empates y
  un material en tres zonas), orden, y crafts de intermedios con y sin "Deduct what I own" (incluido el sobrante de un
  craft que sirve para otro).
- En el juego: una lista con materiales de varias zonas, un material de varias zonas, uno de vendedor, uno de drop, un
  intermedio y cristales; alternar Árbol / Por origen y "Deduct what I own"; plegar grupos; cerrar y abrir la ventana.

## Llamadas del diseño (no pedidas explícitamente; a revisar)

- Vista por defecto: Árbol.
- Grupo "Craftear antes" (no estaba en el pedido; sale gratis del cálculo y completa el "qué tengo que hacer").
- Grupo "Ya tenés todo" separado, para que las zonas muestren solo lo que hay que ir a buscar.
- Recolección tiene prioridad sobre vendedor.
- Los intermedios que ya tenés completos no aparecen en "Por origen" (el cálculo los usa y no se descomponen); el
  árbol los sigue mostrando.
- Grupos plegados: solo mientras la ventana está abierta.

## Queda a criterio de quien implemente

- El control exacto del selector (dropdown de dos opciones o dos botones tipo pestaña), mientras entre entre la
  casilla y "Clear list".
- Cómo se marca visualmente una fila de encabezado (fondo, tamaño de letra) dentro del estilo de la ventana.
