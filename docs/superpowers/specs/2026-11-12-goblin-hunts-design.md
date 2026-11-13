# Goblin Hunts: tabla mínima de hunts y FATEs avisados por Sonar

Fecha: 2026-11-12 · Tweak: `GoblinHunts` · Nodos: `UI/Nodes/HuntTableNode` (nombre a criterio) · Ajustes: `UI/HuntsSettingsAddon`

## Intención

El usuario usa [Sonar](https://github.com/FFXIV-Sonar/SonarDistrib) para enterarse de los hunts (rank A y S) y FATEs.
Hoy, cuando Sonar avisa uno, tiene dos caminos y los dos son lentos:

- En el chat: hace clic en el enlace, se abre el mapa con el flag y tiene que buscar a mano la aetherita y teletransportarse.
- En la ventana de Sonar: busca la fila, abre el desplegable y aprieta el tercer botón naranja (rombo) para ir a la
  aetherita más cercana.

Quiere una **ventanita chica estilo Goblin Damage Done (GDD)** que liste lo que avisa Sonar, con el estado de un
vistazo y dos botones: abrir el mapa con el flag y teletransportarse. Es un **ayudante** de Sonar, no un reemplazo: sin
Sonar instalado la tabla queda vacía.

Sale bien si, cuando aparece un hunt, lo ve en la tabla, escucha un aviso y con un clic está en la aetherita correcta
(o en Ul'dah con el aviso de a qué mundo ir).

## Objetivos

- Tabla nativa (KamiToolKit, nunca ImGui) sobre el HUD, con el mismo aspecto y comportamiento que GDD: movible,
  bloqueable, scroll con la rueda, opacidad, tamaño de fuente y escala configurables.
- Una fila por hunt/FATE, del **más reciente al más viejo**.
- Botón de filtro en el encabezado: **All → S → A → FATE → All**.
- Botones por fila: **mapa con flag** y **teleport** (aetherita más cercana, o Ul'dah si está en otro mundo).
- **Sonido** configurable cuando aparece una entrada nueva, con interruptor para activarlo o no.
- Dos fuentes de datos: el **chat de Sonar** y la **memoria de Sonar** (más la vida en vivo del juego cuando el hunt
  está cerca). Si Sonar cambia y no se puede leer, **nunca crashea**: esa fuente se apaga y la tabla sigue con lo que haya.
- Ícono propio del tweak, ventana de ajustes y ayuda, en inglés y español.

## No objetivos

- No se conecta al servidor de Sonar ni a ningún otro servidor. No envía nada a ningún lado.
- No reemplaza la configuración de Sonar: qué servers, rangos y alcances entran lo decide Sonar. El único filtro
  propio es el botón All/S/A/FATE.
- No hace el viaje entre mundos (World Visit). Solo lleva a Ul'dah y dice a qué mundo ir.
- No hay rank B (el filtro de Sonar tiene B, este no).
- No modifica nada de Sonar (no escribe en su memoria, no cambia su configuración, no borra sus entradas).

## Anti-objetivos (sería un fracaso aunque funcione)

- Que una actualización de Sonar haga crashear el juego o deje GoblinTweaks en estado de error. Cualquier fallo de
  lectura se atrapa; el tweak sigue activo y la tabla queda vacía o con lo del chat.
- Que la tabla tape las ventanas del juego: va en la capa `OverlayLayer.BehindUserInterface`, como GDD desde el último
  cambio (ver `DamageMeterNode`, `SidePanelNode`).
- Que el teleport lleve a una zona del mundo equivocado sin avisar.

## Cómo se ve (decidido con el usuario)

Boceto aprobado: opción A, versión 3 (`.superpowers/brainstorm/937-1794532386/content/layout-v3.html`).

**Encabezado:** `Goblin Hunts (<mundo actual>)` — el mundo donde el jugador está parado ahora (cambia al visitar otro
mundo). A la derecha del título: botón de filtro que muestra el filtro activo (`All`, `S`, `A`, `FATE`), y los
botones de bloquear, ajustes y cerrar, como GDD.

**Fila (una línea):**

```
[S] [18] Bone Crawler     100%   The Peaks      Jenova     [🗺] [◆]
```

1. Ícono de tipo: cuadradito con letra. **S** y **A** con su color; **F** en **morado** para FATEs.
2. Jugadores cerca entre corchetes, `[18]`. Solo si se conoce (sale de la memoria de Sonar); si no, no se dibuja.
3. Nombre del hunt o de la FATE (recortado con "..." si no entra).
4. Porcentaje: vida para hunts, progreso para FATEs.
5. Zona (sin coordenadas). Si el hunt está en una instancia (①②③), el número va al lado de la zona.
6. Mundo. **En amarillo** si es distinto del mundo actual del jugador.
7. Botón **mapa**: abre el mapa con el flag en la posición.
8. Botón **teleport**: ver "Teleport". Deshabilitado (gris) en entradas muertas o terminadas.

**Color de la fila** (barra detrás de la fila, como las barras de GDD, cuyo ancho es el porcentaje):

| Estado | Color | Barra |
|---|---|---|
| Hunt vivo, intacto (100%) | verde | llena |
| Hunt al que ya le pegan | naranja | ancho = vida |
| Hunt muerto | rojo, nombre y zona tachados | llena |
| FATE en curso / en preparación | morado | ancho = progreso |

## Comportamiento

**Orden:** del más reciente al más viejo según **cuándo se encontró** (la primera vez que apareció), no la última
actualización. Una entrada que cambia de vida no sube.

**Scroll:** igual que GDD (`ScrollMain` / `OnScroll` en `GoblinDamageDone`): se ven hasta "Filas como máximo" filas y la
rueda del mouse desplaza el resto.

**Filtro:** el botón cicla All → S → A → FATE → All; se guarda en los ajustes.

**Vencimiento:**

- Hunt muerto: queda en rojo **5 minutos** y desaparece.
- FATE terminada (completada o fallida): desaparece enseguida. Si solo se conoce por el chat (que nunca avisa el final),
  desaparece a los **30 minutos** de anunciada.
- Hunt que nadie actualiza: desaparece a la **hora** de su última actualización.

Estos tiempos son los mismos que los valores por defecto de Sonar (`DisplayHuntDeadTimer` = 300 s,
`DisplayHuntUpdateTimer` = 3600 s, `DisplayFateUpdateTimer` = 1800 s). Si se lee la vista de Sonar, se respetan los
tiempos que el usuario tenga configurados allí. Si se cae al respaldo (`Data.States` crudo, que nunca quita nada), o la
entrada vino solo del chat, se aplican estos tiempos propios.

**Sonido:** cuando entra una entrada nueva que pasa el filtro activo, suena el efecto de sonido de chat elegido
(`UIGlobals.PlayChatSoundEffect(n)`, como `GoblinSniper`/`RetainerVentureTimer`). Ajustes: interruptor "Aviso con
sonido" y número de sonido (1–16, los `<se.N>` del juego). Al cambiar el número en ajustes, suena una vez para probarlo.
Una entrada que llega por las dos fuentes suena una sola vez. Al activar el tweak, las entradas que ya estaban en Sonar
no suenan (solo las que aparecen después). *(decisiones mías: que respete el filtro, que no suene lo ya existente)*

**Sin Sonar:** el tweak se activa igual; la tabla muestra el encabezado y queda vacía.

## Fuentes de datos

Las dos alimentan una sola lista, unificada por (tipo, id de hunt/FATE, mundo, zona, instancia). Si las dos tienen la
misma entrada, manda la memoria (es más completa).

### 1. Chat de Sonar

`Svc.Chat.ChatMessage`, mensajes cuyo remitente es `Sonar` (el canal lo elige el usuario en Sonar; por defecto Echo).
Formato, según `SonarPlugin/Game/PayloadExtensions.cs` y `Notifiers/HuntNotifier.cs` / `FateNotifier.cs`:

- Hunt: `Rank <A|S|SS|...>: <Nombre> ` + **MapLinkPayload** (zona, mapa, coordenadas) + instancia opcional (ícono
  ①..⑨) + ` <` + ícono de cross-world opcional + `<Mundo>>`.
- Hunt muerto: el mismo mensaje con ` was just killed` al final (Sonar lo manda por defecto).
- FATE: `FATE: <Nombre> ` + MapLinkPayload + ... + estado (`(N% with … remaining)` o `(in Preparation)`). Sonar solo
  anuncia en el chat las FATEs que el usuario marcó en su configuración (por defecto ninguna) y **nunca** el final.

Zona y posición salen del `MapLinkPayload`, no del texto. El mundo, del texto entre `< >` (buscar el `World` por nombre
en la hoja de datos). Rango solo A y S; `SS` y `SSMinion` (los secuaces que anuncian un SS) se muestran como S
*(decisión mía)*.

Idioma: solo las palabras fijas vienen en inglés (`Rank X:`, `FATE:`, ` was just killed`, `(in Preparation)`, `with …
remaining`). El nombre del hunt o la FATE viene en el idioma de la base de datos de Sonar, que por defecto es el
idioma del cliente del juego (`SonarPluginIoC.Client.cs` fija `Database.DefaultLanguage`). Parsear por los prefijos en
inglés y comparar nombres contra los datos del juego en todos los idiomas del cliente, tanto para unir entradas del
chat con las de memoria como para encontrar el bicho en `Svc.Objects`. En la tabla, el nombre se muestra tal cual llega.

### 2. Memoria de Sonar (reflexión)

El usuario lo pidió explícitamente sabiendo que no es una API pública de Sonar y que puede romperse con cada versión.
Todo acceso por reflexión (no se puede referenciar el ensamblado de Sonar: vive en otro `AssemblyLoadContext`).

Camino, según el código de SonarDistrib (build `15.757.0.1`):

1. Encontrar la instancia del plugin `SonarPlugin.SonarPluginIoC` (nombre interno `SonarPlugin` o
   `SonarPlugin-preview`). `IDalamudPluginInterface.InstalledPlugins` solo da nombre y versión; la instancia está en
   los internos de Dalamud (`PluginManager` → `LocalPlugin` → campo de la instancia). Usar reflexión sobre Dalamud.
2. Propiedad privada `Client` (`Sonar.SonarClient`) → `Trackers` (`RelayTrackers`) → `Hunts` y `Fates`.
3. Cada tracker → `Data.States` (diccionario clave → `RelayState<T>`).
4. Por `RelayState`: `Relay`, `LastFound`, `LastSeen`, `LastKilled` (timestamps Unix en segundos, double).
5. `HuntRelay`: `Id`, `WorldId`, `ZoneId`, `InstanceId`, `Coords`, `CurrentHp`, `MaxHp`, `HpPercent`, `IsPulled`,
   `Players`, `IsAlive()`. Rango y nombre: `Relay.Info` (`IRelayDataRow`, con `Name` y en hunts `Rank`).
6. `FateRelay`: `Progress`, `Status` (`Running`, `Preparation`, …), `Players`, `IsAlive()`.

Qué entra: lo que Sonar le mostraría al usuario. Lo ideal es leer la vista de la ventana principal de Sonar
(`SonarPlugin/Trackers/RelayTrackerViews`, resuelta del contenedor DryIoc) para respetar su configuración. Si eso es
inalcanzable, usar `Data.States` filtrando solo A/S/FATE. *(a criterio del constructor)*

Lectura en el hilo del juego (`Svc.Framework.Update`), como mucho una vez por segundo (patrón `DateTime` de CLAUDE.md).

**Tolerancia a fallos (obligatoria):**

- Todo el lector de memoria dentro de `try/catch`. Un fallo **no** llama a `ReportFailure` (eso desactivaría el
  tweak): apaga solo esta fuente, escribe una vez un `Svc.Log.Warning` con el motivo, y reintenta cada 30 segundos
  (por si Sonar terminó de cargar o se recargó). *(30 s es decisión mía)*
- Cachear los `PropertyInfo`/`FieldInfo` resueltos; si Sonar se descarga o recarga (instancia distinta), volver a
  resolver.
- Nunca guardar referencias a objetos de Sonar entre frames: copiar los valores a registros propios.
- No escribir nada en Sonar.

### 3. Vida en vivo del juego

Si el hunt está en el mundo, zona e instancia actuales del jugador, buscarlo en `Svc.Objects` (BattleNpc cuyo nombre
coincide) y tomar vida actual/máxima de ahí; pisa a las otras fuentes mientras esté visible. Esto da naranja y
porcentaje aun cuando la memoria de Sonar no se pudo leer.

## Teleport

- **Mismo mundo que el actual:** aetherita **más cercana** a la posición del hunt en su zona, entre las que el jugador
  tiene sintonizadas. Calcularla con las hojas `Aetheryte` + posiciones del mapa (`MapMarker`/`Level`), y
  teletransportar con `Telepo.Instance()->Teleport(aetheryteId, subIndex)` (FFXIVClientStructs). Si ninguna está
  sintonizada, mensaje en el chat y no hace nada.
- **Otro mundo:** teletransportar a **Ul'dah** (aetherita de Ul'dah - Steps of Nald) y mostrar un aviso en pantalla
  (toast) y en el chat: «<Nombre> está en <Mundo>: usá «Visitar otro mundo» en la aetherita de Ul'dah». Si ya está en
  Ul'dah, solo el aviso. Desde la 7.58, todos los data centers de la región (NA o EU) se alcanzan con «Visitar otro
  mundo», así que esto cubre todo lo que Sonar muestra con su configuración por defecto. Si el mundo es de otra región
  (solo posible si el usuario puso en Sonar el alcance Audience o All), igual lleva a Ul'dah, pero el aviso dice que
  hace falta Data Center Travel.
- **Mismo mundo y zona, otra instancia:** teletransportar igual a la aetherita más cercana y avisar (toast) a qué
  instancia cambiar.
- Si no se puede teletransportar (en combate, en duty, casteando), el juego lo rechaza; mostrar el motivo si se conoce.

## Mapa

Botón mapa: abrir el mapa con el flag en zona + posición (lo mismo que hacer clic en el enlace del chat). Por ejemplo
`Svc.GameGui.OpenMapWithMapLink(new MapLinkPayload(...))`.

## Ajustes (ventana `SettingsPanelAddon`, abre con ⚙ y con el botón "Ajustes")

| Sección | Opción | Por defecto |
|---|---|---|
| Tabla | Filas como máximo | 8 |
| Tabla | Bloquear posición | no |
| Aviso | Aviso con sonido | sí |
| Aviso | Sonido (1–16) | 6 *(el mismo de GoblinSniper)* |
| Apariencia | Opacidad del fondo (%) | 62 |
| Apariencia | Tamaño de fuente | 14 |
| Apariencia | Tamaño de la tabla (%) | 100 |

Más los botones "Restablecer posición" y el idioma del tweak, como GDD. Posición y filtro activo se guardan en los
`Options` (como `OffsetX/OffsetY`, `Mode` de GDD).

## Ayuda

Botón "Ayuda" (`HelpButton`, ventana `TextHelpAddon` como GDD) con temas que expliquen:

- Qué es: ayudante de Sonar; necesita Sonar instalado; no se conecta a ningún servidor.
- La tabla: colores, corchetes de jugadores, porcentaje, mundo en amarillo.
- Botones: mapa y teleport, y qué pasa si el hunt está en otro mundo.
- Filtro, orden, scroll y cuándo desaparecen las entradas.
- De dónde salen los datos: el chat de Sonar (activar en Sonar los avisos en el chat y, para FATEs, marcar cuáles), la
  lectura de Sonar (puede dejar de funcionar si Sonar se actualiza; entonces faltan jugadores y vida a distancia) y la
  vida en vivo cuando estás cerca.

## Ícono y comando

- Comando corto `/ghunt` y largo `/goblinhunts` (abre/cierra la tabla) *(nombres míos)*. El ícono se toma de
  `Assets/IconTweaks/ghuntIcon.png` (`Tweak.IconKey` = comando corto + `Icon`).
- Ícono 128×128 RGBA, mismo estilo que los demás (cuadrado oscuro redondeado con borde dorado, dibujo plano), por
  ejemplo una mira o un cartel de "wanted" con una S. *(a criterio del constructor)*
- Categoría: `TweakCategory.Other` *(decisión mía, como GoblinSniper)*.

## Restricciones

- UI siempre nativa (KamiToolKit). Nodos en el `SharedOverlay` vía `OverlayNodes`; nunca `new OverlayController()`.
- `Enable`/`Disable` en el hilo del juego; `Disable` deshace todo (nodos, eventos de chat y framework, comandos).
- Antes de liberar nodos de una ventana abierta, `NativeFocus` según las reglas del proyecto.
- Todos los textos en `en.json` y `es.json` (`Tweaks.GoblinHunts.*`).
- Sin hooks: chat, framework, objetos y reflexión alcanzan.

## Lo que queda al constructor

- Estructura interna de clases y nodos (puede reutilizar piezas de `DamageMeterNode`, `MeterRows`, `SidePanelNode`).
- Cómo llegar a la instancia de Sonar en los internos de Dalamud y a su vista principal.
- Cómo calcular la aetherita más cercana y el id/subíndice correctos para `Telepo`.
- Dibujo del ícono.
