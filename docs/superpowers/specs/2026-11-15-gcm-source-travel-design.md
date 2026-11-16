# Crafting Materials "Por origen": botones de mapa y teleport

Fecha: 2026-11-15 · Tweak: `CraftingMaterials` · Ventana: `UI/CraftingMaterialsAddon` (vista "By source" de My List)
Sigue a: `2026-11-15-gcm-totals-by-source-design.md`

## Intención

La vista "Por origen" agrupa por zona lo que hay que recolectar, para no repetir viajes. El paso siguiente es ir:
hoy hay que abrir el mapa, buscar el punto, teletransportarse a mano y cambiar de job. El usuario quiere hacerlo desde
la fila del material, con dos botones como los de Goblin Hunts.

## Objetivos

- En cada fila de material de un **grupo de zona**, al final de la fila, dos botones:
  - **Mapa**: abre el mapa de la zona con el **área de recolección** dibujada como la dibuja el Gathering Log (el
    círculo con "Lv. 35 Mineral Deposit"), no un Flag.
  - **Teleport**: teletransporta a la aetherita sintonizada más cercana al área, deja el área marcada en el mapa y,
    **al llegar a la zona** (terminada la carga), cambia al job de la clase que recolecta ese material (Miner,
    Botanist o Fisher).
- Mismo aspecto que los botones de Goblin Hunts (cuadrado azul con un mapa plegado, cuadrado naranja con la
  aetherita).

## No objetivos

- Botones en el encabezado de zona (opción B descartada por el usuario: con Miner y Botanist en la misma zona no hay un
  job único).
- Botones en "Ya tenés todo", "Comprar a vendedor", "Otros", "Craftear antes" y "Cristales".
- Horarios de los puntos con horario, rutas, viaje a otro mundo, montar o volar hasta el punto.
- Cambiar la vista Árbol.

## Anti-objetivos

- Un crash o un cambio de job a destiempo: el cambio de job nunca ocurre durante el casteo del teleport, en combate ni
  en una carga.
- Cambiar el job si el teleport no se hizo (rechazado, cancelado) o si el jugador terminó en otra zona.
- Romper el scroll de la lista o el tooltip / menú de click derecho de la fila.

## Restricciones del proyecto

- UI nativa KamiToolKit, nunca ImGui.
- Textos en inglés y español en `UI/CraftLoc.cs`.
- Lógica sin Dalamud (cálculos de coordenadas, elección de área y de gearset) en carpetas que compila
  `GoblinTweaks.Tests` (`Crafting/` o `Hunts/`), con tests.
- Áreas con tooltip que se ocultan y vuelven a mostrar: KTK les saca el flag de colisión y el juego no las vuelve a
  tomar hasta reconstruir la lista de colisiones (memoria "Goblin Hunts"). Las filas de `ListNode` se reciclan, así que
  los botones se muestran y ocultan seguido.
- Liberar nodos de una ventana abierta pasa por `NativeFocus` (memoria "Native focus crash").

## Enfoques considerados

- **A. Funciones del juego para el área + teleport de Goblin Hunts + gearset al llegar (elegido).** `AgentMap`
  tiene `AddGatheringTempMarker(mapX, mapY, radius, iconId, styleFlags, tooltip)` y
  `OpenMap(mapId, territoryId, title, MapType.GatheringLog)`: lo que usa el Gathering Log. El centro y el radio de cada
  punto están en `ExportedGatheringPoint` (fila = `GatheringPointBase`), en `FishingSpot` (X, Z, Radius) y en
  `SpearfishingNotebook` (X, Y, Radius). El teleport reutiliza la búsqueda de aetherita de `HuntTravel`. El job se
  cambia con `RaptureGearsetModule.EquipGearset`.
- **B. Flag (`SetFlagMapMarker`) en vez del área.** Más simple, pero no es lo pedido. Queda como respaldo solo si el
  área no puede dibujarse.
- **C. Cambiar el job con el comando `/gearset change` por chat.** Más frágil que llamar a la función; descartado.
- **D. Cambiar el job antes del teleport.** Interrumpe el casteo y el usuario prefiere que sea al llegar; descartado.

## Diseño

### Lo que se ve

```
▾ Yak T'el · Miner                                      2 to get
    Cobalt Tungsten Ore     0/2   Xmun Hojaw         120  [▤][◆]
    Raw Pink Beryl          0/6   Iq Rrax Tsoly          [▤][◆]
▾ Comprar a vendedor
    Annite Whetstone ...                              80
```

- Dos botones de 18×18 (como Goblin Hunts) a la derecha de la columna de precio, en las filas de material de los
  grupos de zona que tienen área conocida. La columna del nombre se angosta para hacerles lugar; las demás columnas no
  se mueven. En las filas sin botones (otros grupos, encabezados) ese espacio queda vacío, así las columnas siguen
  alineadas con los títulos.
- Tooltips: Mapa: "Open the map with the gathering area" / "Abrir el mapa con el área de recolección". Teleport:
  "Teleport to the nearest aetheryte, mark the area and change to {clase}" / "Teletransportarse a la aetherita más
  cercana, marcar el área y cambiar a {clase}".
- El resto de la fila se comporta igual que hoy (tooltip del material, click derecho con el menú de item).

### Qué áreas se marcan

Para el material y la zona de su grupo, **todas** sus áreas en esa zona (todos los puntos donde sale ahí, de la misma
clase que la fila). El índice de recolección (`GatheringSources`) guarda además, por item y zona, la lista de áreas:

- minería / botánica / arpón: por cada `GatheringPoint` del item, la fila de `ExportedGatheringPoint` de su
  `GatheringPointBase` (centro X/Y, radio). Varios puntos con la misma base son una sola área. Arpón: también
  `SpearfishingNotebook` si `ExportedGatheringPoint` no tiene fila.
- pesca: el `FishingSpot` (X, Z, radio).
- el mapa: `TerritoryType.Map` de la zona.
- la etiqueta: "Lv. {nivel} {tipo de punto}" como en el juego (ej. "Lv. 35 Mineral Deposit"); el tipo sale del
  nombre del `GatheringType` (o del texto del juego que use el Gathering Log, si se encuentra); el ícono, de
  `GatheringType.IconMain` (pesca: el ícono de pesca).

Áreas sin datos (fila inválida, radio 0, sin mapa): se ignoran. Si el material no tiene ninguna área, la fila no
muestra botones.

### Botón Mapa

1. Borrar las marcas temporales anteriores del mapa (las del Gathering Log o de un click anterior).
2. Por cada área: `AddGatheringTempMarker` con centro, radio, ícono y etiqueta.
3. `OpenMap(mapa, zona, título = nombre del material, MapType.GatheringLog)`.

Las coordenadas que esperan estas funciones (unidades de mundo, de mapa o de textura) se confirman en el juego en la
primera prueba: la conversión vive en una función pura con tests, para corregirla en un solo lugar.

### Botón Teleport

1. Marcar las áreas como en el botón Mapa, sin abrir el mapa.
2. Elegir la aetherita: la sintonizada más cercana a cualquiera de las áreas de esa zona (misma búsqueda que
   `HuntTravel`, incluidas las aetherytes "incómodas" y la excepción de Dravanian Hinterlands). Se teletransporta
   aunque ya estés en esa zona (como Goblin Hunts).
3. `Telepo.Teleport`. Resultado:
   - **rechazado** (combate, duty, casteando, etc.) → aviso "No se pudo teletransportar" y nada más;
   - **sin aetherita sintonizada en esa zona** → aviso con el nombre de la zona;
   - **aceptado** → queda pendiente el cambio de job.
4. **Cambio de job pendiente**: se resuelve en el `Framework.Update`:
   - espera ver una carga de zona (`BetweenAreas`) y su fin;
   - al terminar, si la zona actual es la del área, el jugador está cargado y no está en combate ni casteando, se
     vuelven a poner las marcas del área (por si la carga las borró) y se cambia de job;
   - si se llega a otra zona, o pasan 60 segundos desde el click sin llegar, se cancela sin aviso;
   - un nuevo click de Teleport reemplaza el pendiente anterior; deshabilitar el tweak lo cancela.
5. **Qué gearset**: el de la clase (Miner 16, Botanist 17, Fisher 18; `ClassJob`) con mayor nivel de item; empate, el
   de número más bajo. Si ya estás en esa clase, no se cambia. Si no hay gearset de esa clase → aviso "No tenés un
   gearset de {clase}".

Los avisos se muestran como en Goblin Hunts: toast normal y línea en el chat con el nombre del tweak.

### Separación del código

- `Crafting/` o `Hunts/` (sin Dalamud, con tests): conversión de coordenadas del área a coordenadas de mapa (para
  elegir la aetherita) y a las del marcador; elección del gearset a partir de una lista (id, clase, nivel de item);
  la máquina de estados del cambio pendiente (entradas: hubo carga, terminó, zona actual, en combate, tiempo
  transcurrido; salida: esperar / cambiar / cancelar).
- `Core/GatheringSources.cs`: las áreas por item y zona.
- `Services/` (con Dalamud): `GatherTravel` con `OpenMap(...)`, `Teleport(...)` y el pendiente; reutiliza de
  `HuntTravel` la lista de aetherytes sintonizadas y la búsqueda de la más cercana (extraer lo común sin cambiar el
  comportamiento de Goblin Hunts).
- `UI/Nodes/IngredientRowNode.cs`: los dos botones (fondo, glifo, área de click con tooltip), visibles solo si la fila
  trae áreas; callbacks estáticos como `OnItemRightClick`.
- `UI/CraftingMaterialsAddon.cs`: pasar a la fila lo necesario (item, zona, clase) en las filas de grupos de zona.

### Textos

En `CraftLoc` (inglés y español, voseo): los dos tooltips, los avisos (rechazado, sin aetherita, sin gearset) y una
línea en la ayuda de la ventana sobre los botones.

## Pruebas

- Unitarias: conversión de coordenadas (casos con `SizeFactor` 100 y 200 y offsets), elección de gearset (ninguno,
  varios, empate, ya en la clase), máquina de estados del pendiente (llega a la zona, otra zona, timeout, rechazo,
  combate al llegar espera, nuevo click reemplaza).
- En el juego: botón Mapa en un material de minería, uno de botánica, uno de pesca y uno con varias áreas en la zona;
  que el círculo quede donde lo dibuja el Gathering Log; Teleport desde otra zona y desde la misma zona; sin gearset de
  la clase; en combate; scroll con el mouse sobre los botones; tooltip y click derecho de la fila; filas recicladas al
  plegar y desplegar grupos.

## Llamadas del diseño (no pedidas explícitamente; a revisar)

- Solo filas de grupos de zona.
- Todas las áreas del material en esa zona; teleport a la aetherita más cercana a cualquiera.
- Teleport aunque ya estés en la zona.
- Gearset de mayor nivel de item si hay varios de la clase.
- Cancelación silenciosa a los 60 s o al llegar a otra zona.
- Las marcas del área se vuelven a poner al llegar.

## Queda a criterio de quien implemente

- El glifo exacto de los botones (reutilizar los de Goblin Hunts es lo esperado).
- El color y el detalle de la etiqueta del área si el texto del juego no se encuentra.
- Si `ListNode` necesita que el área de click de los botones registre también la rueda del mouse para no cortar el
  scroll (comprobarlo en el juego; Goblin Hunts lo necesitó).
