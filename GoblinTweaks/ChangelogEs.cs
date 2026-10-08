namespace GoblinTweaks;

/// <summary>
/// Spanish text of the changelog entries, by version. An entry without a Spanish version here is shown in English.
/// Keep it in step with <see cref="Changelog"/>: same versions, same number of changes, same order.
/// </summary>
internal static class ChangelogEs
{
    public static readonly Dictionary<string, (string Summary, string[] Changes)> Entries = new()
    {
        ["1.1.0"] = (
            "Ventana principal nativa nueva con iconos, capturas y comandos de cada tweak; cada tweak tiene su propio idioma.",
            [
                "Change: toda la interfaz ahora es nativa del juego: una ventana principal nueva con lista de tweaks, pestañas, buscador y un panel de detalle con descripción, comandos, ajustes y capturas",
                "New: cada tweak tiene un icono y hasta tres capturas que se abren en grande en su propia ventana, con botones para ir a la siguiente y a la anterior",
                "New: comandos de chat para cada tweak, uno corto y uno largo (por ejemplo /gwp y /gweapon, /gbt y /goblinbattletext), listados en el panel de detalle",
                "New: el Timer de encomiendas de retainer, el Timer de escuadrón y las Marcas de recetas completadas tienen una ventana de ayuda que explica cómo funcionan y dónde mirar (la barra de información del servidor)",
                "New: AutoGoblinRetainer, Goblin Battle Text y Pose con arma por job tienen su propio idioma, independiente del de GoblinTweaks, como ya tenían Crafting Materials y GoblinSniper",
                "New: Crafting Materials tiene un botón de tuerca en su barra de título que abre sus ajustes",
                "Change: descripciones más completas en todos los tweaks, Jupiter como fuente por defecto, y se incluyen solo inglés y español",
                "Fix: la lista de venta de AutoGoblinRetainer volvía al principio cada vez que llegaban los precios de Universalis",
                "Fix: el juego podía cerrarse al activar y desactivar tweaks muchas veces seguidas",
            ]),

        ["1.0.14"] = (
            "Tweak nuevo AutoGoblinRetainer: reajusta y pone a la venta por vos los items de tus retainers en el Market Board.",
            [
                "New: AutoGoblinRetainer — el botón Pinch de la lista de retainers reajusta todos los anuncios de todos los retainers justo por debajo del otro vendedor más barato",
                "New: AutoGoblinRetainer — ventana Vender para marcar items de tu inventario, armoury chest y retainers y ponerlos a la venta en el Market Board, repartiendo los 20 anuncios de cada retainer",
                "New: AutoGoblinRetainer — columna de precio estimada por Universalis (mediana de los últimos 30 días), columnas ordenables, cantidad a vender por item y la opción Pinch tras vender",
                "New: AutoGoblinRetainer — ventana de configuración y ventana de ayuda, ambas nativas del juego",
                "Change: las ventanas de configuración de Crafting Materials y GoblinSniper ahora son nativas del juego, con un desplegable para elegir el idioma",
                "Fix: Crafting Materials asignaba al retainer equivocado los items del último retainer que abriste y olvidaba los demás — cada retainer y el saddlebag se recuerdan ahora entre visitas",
            ]),

        ["1.0.13"] = (
            "Goblin Battle Text: unión de mensajes corregida y ampliada — los críticos se unen, y los fallos, bloqueos, inmunidades y ticks tienen sus propias opciones.",
            [
                "New: Goblin Battle Text los golpes críticos se unen con críticos, y los críticos directos con críticos directos — nunca entre sí",
                "New: Goblin Battle Text opciones para unir fallos y esquivas, golpes bloqueados, parados y resistidos, efectos inmunes y resistidos, y ticks de daño y curación en el tiempo sobre varios objetivos",
                "Change: Goblin Battle Text la unión espera 0,4 segundos al siguiente golpe en vez de 0,35",
                "Change: Goblin Battle Text los golpes defendidos o mitigados de forma distinta ya no se unen en un solo mensaje que muestra la nota solo del primero",
                "Fix: Goblin Battle Text los golpes de una misma acción se partían en varios mensajes (x2, crítico, x2, crítico) cuando entre ellos había un crítico",
            ]),

        ["1.0.12"] = (
            "Goblin Battle Text: degradado para alertas de posicional, posicionales de frente, textos más claros, alemán, francés y japonés.",
            [
                "New: Goblin Battle Text las alertas de posicional pueden usar un degradado entre dos colores, de izquierda a derecha o de arriba abajo",
                "New: Goblin Battle Text se reconocen los posicionales que golpean de frente",
                "New: alemán, francés y japonés para Goblin Battle Text",
                "New: Crafting Materials resalta la pestaña y los slots del baúl de la FC que tienen los ingredientes de la receta seleccionada",
                "Change: Goblin Battle Text el posicional en el mensaje de daño colorea solo el veredicto, no todo el nombre de la acción",
                "Change: textos de Goblin Battle Text y GoblinSniper reescritos para ser más cortos y claros, manteniendo el voseo en los textos en español",
                "Fix: Goblin Battle Text las páginas de ayuda que no entraban en la ventana ya no se cortan — tienen scroll",
            ]),

        ["1.0.11"] = (
            "Goblin Battle Text: configuración por personaje, daño mitigado, mascotas y contraataques con nombre, arreglos en alertas de cooldown.",
            [
                "New: Goblin Battle Text la configuración se guarda por personaje — uno que se ve por primera vez empieza con una copia de la que tenías",
                "New: Goblin Battle Text el daño que recibís indica cuánto fue mitigado, como -270 (-30% mitigado), con solo el porcentaje o sin nada si preferís",
                "New: Goblin Battle Text Mostrar bloqueado, parado y resistido — activá o desactivá la nota después de un golpe así",
                "New: Goblin Battle Text un efecto que no entra lo dice, como Leg Sweep Inmune o Leg Sweep Resistido por completo",
                "New: Goblin Battle Text páginas de ayuda sobre posicionales, daño y curación en el tiempo, y qué cuenta como tuyo",
                "Change: Goblin Battle Text las acciones de tu mascota o chocobo terminan con su nombre, y sus auto-ataques dicen Ataque antes",
                "Change: Goblin Battle Text una acción sin daño que pone un efecto sobre otro, como un aturdimiento, muestra su icono y nombre",
                "Change: Goblin Battle Text las alertas de cooldown omiten los mudras del ninja",
                "Fix: Goblin Battle Text el contraataque de Vengeance o Damnation salía como un Ataque simple — ahora muestra el nombre e icono de su acción",
                "Fix: Goblin Battle Text las alertas de cooldown nombraban la acción mejorada con el nivel sincronizado, como Bloodwhetting por Raw Intuition",
                "Fix: Goblin Battle Text todos los cooldowns se anunciaban como listos tras una pantalla de carga",
                "Fix: Goblin Battle Text el daño en el tiempo de origen desconocido salía como tuyo con (DoT) — un tick solo se muestra cuando algo tuyo está haciendo tick",
                "Fix: Goblin Battle Text el daño en el tiempo de Choco Beak salía sin nombre",
                "Fix: Goblin Battle Text un color perdido de la configuración salía como texto negro — recupera su valor por defecto",
            ]),

        ["1.0.10"] = (
            "Goblin Battle Text: el daño del suelo como Doton muestra su nombre y solo cuando es tuyo.",
            [
                "Fix: Goblin Battle Text el daño del suelo, como Doton, salía como un número solo con (DoT) — ahora muestra el nombre e icono de su efecto",
                "Fix: Goblin Battle Text mostraba el daño del suelo de otros jugadores como tuyo — ahora solo sale el tuyo, incluso junto a alguien de tu trabajo",
                "Change: Goblin Battle Text la curación en el tiempo se distingue por quién la dio, como la informa el juego, en vez de por los efectos de quien la recibe",
            ]),

        ["1.0.9"] = (
            "Goblin Battle Text: alertas de posicional, nombres en daño y curación en el tiempo, solo tus propios debuffs y críticos separados.",
            [
                "New: Goblin Battle Text alertas de posicional — las acciones que pegan más fuerte desde atrás o el flanco dicen si las golpeaste desde ahí, como alerta propia o en el mensaje de daño",
                "New: Goblin Battle Text Destacados tiene un aspecto para un posicional acertado y otro para uno fallado, y muestra un tipo de destacado a la vez",
                "New: Goblin Battle Text los ticks de daño y curación en el tiempo muestran el nombre e icono de su efecto, como Higanbana o Regen",
                "New: Goblin Battle Text los auto-ataques dicen quién los hace cuando no sos vos: tu chocobo, tu mascota o el enemigo",
                "New: Goblin Battle Text Unir curación en varios objetivos, aparte de la opción para el daño",
                "New: Goblin Battle Text Mostrar para quién es mi curación — la curación que das termina con el nombre de su objetivo",
                "Change: Goblin Battle Text los golpes críticos nunca se unen — salen aparte con su propio aspecto, y los directos solo se unen entre sí",
                "Change: Goblin Battle Text el daño en el tiempo compartido con otros jugadores muestra tu parte como estimación, con ~ delante, y no se muestra cuando nada es tuyo",
                "Change: Goblin Battle Text la curación en el tiempo sobre vos siempre sale como recibida, y la que das vos o tu mascota sale como dada",
                "Fix: Goblin Battle Text mostraba los debuffs que otros jugadores ponen a enemigos — ahora solo salen los tuyos, incluso junto al mismo debuff de alguien de tu trabajo",
            ]),

        ["1.0.8"] = (
            "Goblin Battle Text: configuración rehecha en 5 pestañas, áreas que arrastrás en pantalla, colores por tipo de golpe y texto para curas de Bloodbath y dashes.",
            [
                "New: Goblin Battle Text las áreas se arrastran en pantalla desde una caja con su nombre mientras la ventana de configuración está abierta",
                "New: Goblin Battle Text evento Acción sin daño — los dashes y otras acciones para las que el juego no muestra texto aparecen con su icono y nombre",
                "New: Goblin Battle Text los golpes críticos, directos y críticos directos tienen un color para el daño causado y otro para el recibido; los críticos tienen un tercero para la curación",
                "New: Goblin Battle Text la posición tiene una caja numérica con + y - junto a su deslizador, y la alineación tiene botones para 0, 50 y 100",
                "New: Goblin Battle Text puede copiar el aspecto de un área a otra, desde la rueda junto a las áreas",
                "New: Goblin Battle Text Mensajes a la vez está disponible en todas las áreas, de 1 a 15",
                "New: Goblin Battle Text el texto de muestra sigue la pestaña en pantalla — Destacados y Cooldowns muestran solo sus mensajes y reproducen al instante lo que cambiás",
                "Change: Goblin Battle Text la configuración pasa de 8 pestañas a 5 — las tres áreas comparten una pestaña con el selector del propio juego, los colores están en General y todo sobre la alerta de cooldown está en Cooldowns",
                "Change: Goblin Battle Text las animaciones se eligen por tipo (Suave, Impactos, Tamaño, Rotación, Movimiento, Luz) y luego por animación",
                "Change: Goblin Battle Text las acciones de cooldown son botones con icono y una caja de búsqueda, iluminados cuando se anuncian",
                "Change: Goblin Battle Text el orden de cada mensaje es una fila de partes, y solo lista las partes que usan los eventos enviados al área",
                "Change: Goblin Battle Text el botón Restaurar hay que mantenerlo pulsado, y la duración se muestra en segundos",
                "Fix: Goblin Battle Text no mostraba cuánto te curan efectos como Bloodbath, que el juego seguía mostrando como su propio texto",
            ]),

        ["1.0.7"] = (
            "Goblin Battle Text: destacados para golpes críticos y directos con 35 animaciones, orden de mensaje por área y texto que ya no se cruza.",
            [
                "New: Goblin Battle Text pestaña Destacados — los críticos, directos, críticos directos y la alerta de cooldown tienen cada uno su fuente, tamaño, color, animación e intensidad",
                "New: Goblin Battle Text tiene 35 animaciones para mensajes destacados, de Pop a Meteor y Fury",
                "New: Goblin Battle Text muestra los críticos directos con !!, aparte de los críticos (!)",
                "New: Goblin Battle Text el orden de cada mensaje se define por área (Saliente, Entrante, Centro)",
                "New: Goblin Battle Text los buffs y debuffs tienen un color al empezar y otro al terminar",
                "New: Goblin Battle Text tiene un botón para restaurar los valores por defecto de la pestaña en pantalla",
                "New: Goblin Battle Text el área Centro puede limitar cuántos mensajes se muestran a la vez (1 a 10)",
                "New: Goblin Battle Text agrega la fuente Trump Gothic Italic",
                "Change: Goblin Battle Text ya no cruza el texto — los eventos que suben y bajan en una misma área ocupan cada uno la mitad, y los estáticos se apilan fuera del recorrido",
                "Change: Goblin Battle Text el área Centro es estática por defecto",
                "Change: Goblin Battle Text se quitaron las opciones Ocultar el texto del propio juego y Seguir a mi personaje — el texto del juego siempre se oculta y las áreas quedan alrededor del centro de la pantalla",
                "Change: Goblin Battle Text ya no muestra icono de acción en los auto-ataques ni en lo que te hacen los NPC; las acciones de otros jugadores (PvP) conservan el suyo",
                "Fix: Goblin Battle Text los iconos de buffs y debuffs salían aplastados",
                "Fix: Goblin Battle Text las listas desplegables mostraban un nombre interno al estar cerradas",
                "Fix: Goblin Battle Text el título del tema de ayuda ya no se sale de la lista de temas",
            ]),

        ["1.0.6"] = (
            "Goblin Battle Text nuevo: tu daño, curación, buffs y cooldowns se desplazan alrededor de tu personaje. GoblinSniper agrega una columna de reventa.",
            [
                "New: Goblin Battle Text — el daño y la curación que causás y recibís se desplazan junto a tu personaje en vez de sobre quien fue golpeado",
                "New: Goblin Battle Text tiene tres áreas (Saliente, Entrante, Centro) con posición, tamaño, duración, alineación y recorrido, incluido uno estático",
                "New: Goblin Battle Text eventos — el daño, la curación, el MP, los buffs y debuffs se pueden apagar, enviar a cualquier área y desplazar hacia arriba, abajo o dejar quietos",
                "New: Goblin Battle Text alertas de cooldown, apagadas por defecto, con una lista de las acciones de tu trabajo actual",
                "New: Goblin Battle Text muestra iconos de acción y de tipo de daño, con el orden y la visibilidad de cada parte de un mensaje configurables",
                "New: Goblin Battle Text colores por tipo de texto, una ventana de ayuda y el comando /gbt",
                "New: GoblinSniper columna Reventa — probabilidad de que el item se venda en tu mundo en una semana, con orden por ella",
                "New: GoblinSniper ignora los items que nadie compró en demasiado tiempo (60 días por defecto, configurable)",
                "Fix: Selector de color — las texturas del selector de color nativo ahora vienen con el plugin",
            ]),

        ["1.0.5"] = (
            "Nuevos: buscador de oportunidades del market board GoblinSniper y Pose de arma por trabajo, más una estimación de memoria en cada tweak.",
            [
                "New: GoblinSniper — busca en Universalis anuncios muy por debajo del precio normal del item y los lista en una ventana nativa con búsqueda, orden y filtros por mundo y antigüedad de los datos",
                "New: GoblinSniper muestra la cantidad de oportunidades en la barra de información del servidor; hacé clic para abrir la ventana",
                "New: configuración de GoblinSniper — descuento, precio mínimo, intervalo de escaneo, antigüedad máxima de los datos, 14 tipos de item, items concretos e idioma de los nombres",
                "New: Pose de arma por trabajo — recuerda el /cpose elegido con el arma desenvainada para cada trabajo y lo restaura al cambiar",
                "New: cada tarjeta de tweak muestra la memoria aproximada que usa",
            ]),

        ["1.0.4"] = (
            "Crafting Materials agrega seguimiento de HQ en los tooltips, marcas de recetas fuera del log, orden por precio de mercado y una vista Todas las recetas.",
            [
                "New: los tooltips de los ingredientes muestran cantidades HQ por ubicación — por ejemplo \"Retainer: 5 (2 HQ)\"",
                "New: las recetas fuera del log (libros maestros y especiales no de vivienda) muestran un punto gris ●; el nuevo filtro \"Ocultar fuera del log\" las quita",
                "New: orden por precio de mercado vía Universalis — carga en segundo plano, los precios desconocidos van al final",
                "New: la entrada \"Todas las recetas\" en la barra lateral ESTADO muestra todas las recetas sin importar el estado de crafteo",
                "Fix: hacer clic derecho en un ingrediente amarillo (saddlebag) abre el menú del item; en uno rojo crafteable abre su receta en el Crafting Log",
                "Fix: el pulgar de la barra de scroll ahora tiene un tamaño mínimo y nunca se encoge hasta casi desaparecer en listas grandes",
            ]),

        ["1.0.3"] = (
            "Crafting Materials ahora recuerda los datos de retainers y del baúl de la FC entre sesiones y se actualiza solo cuando cambia tu inventario.",
            [
                "New: la instantánea del inventario se guarda en disco — los items de retainers y del baúl de la FC se recuerdan entre sesiones sin tener que visitarlos de nuevo",
                "New: las filas de ingredientes muestran un tooltip con el desglose (al pasar el cursor) de cuántos items tenés en cada inventario cuando están repartidos en varias fuentes",
                "Fix: la lista de ingredientes se actualiza sola cuando movés items con la ventana abierta (1,5 s de espera)",
                "Fix: la receta seleccionada y su panel de ingredientes se conservan en todos los tipos de actualización (manual, automática y tras sintetizar)",
                "Fix: la marca de crafteado se actualiza al instante cuando termina una síntesis",
            ]),

        ["1.0.2"] = (
            "Se agregaron timers en la barra de información del servidor para las encomiendas de retainers y las actividades del escuadrón.",
            [
                "New: Timer de encomiendas de retainer — cuenta regresiva en la barra de información del servidor para el primer retainer que vuelve de una encomienda; pasá el cursor para ver el detalle por retainer",
                "New: Timer de misiones y entrenamiento del escuadrón — cuenta regresiva en la barra de información para misiones y sesiones de entrenamiento; el cursor muestra ambos timers cuando están activos",
            ]),

        ["1.0.1"] = (
            "Se agregó un atajo en el menú contextual del inventario y una ventana de configuración propia para Crafting Materials.",
            [
                "New: al hacer clic derecho en cualquier item del inventario aparece un atajo a \"Crafting Materials\" en el menú",
                "New: Crafting Materials tiene su propia ventana de configuración (selector de idioma, fuentes de inventario)",
            ]),

        ["1.0.0"] = (
            "Lanzamiento inicial con el Inventario de Crafting Materials y las Marcas de recetas completadas.",
            [
                "New: Inventario de Crafting Materials — cruza cada receta con tu inventario, saddlebag, retainers y baúl de la FC",
                "New: Marcas de recetas completadas — agrega marcas del Crafting Log a las filas ya crafteadas de la ventana de recetas",
            ]),
    };
}
