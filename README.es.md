<picture>
  <img src="docs/media/hero.gif" width="1280" alt="La barra de LucentDock al pie del escritorio: una reunión llega a diez minutos de distancia y el dock se reorganiza solo - los widgets de música, clima y tiempo de pantalla se apartan, el calendario pasa al frente con la reunión visible y la barra queda en su disposición de reunión, más tranquila.">
</picture>

# LucentDock

**Un dock inteligente para Windows que se reorganiza solo cuando algo te necesita**, para quien
vive entre reuniones, temporizadores y límites, no solo entre iconos de aplicaciones. Versión 1.3.2.¹

**[Descargar (primera versión en camino)](https://github.com/LeonelIvanCortesRodriguez/LucentDock/releases)** ·
[Ver la historia (≈2 min)](docs/media/story-es.mp4) ·
[In English](README.md) ·
[Todas las funciones](docs/FEATURES.md)

## El problema

La barra de tareas de Windows muestra tus aplicaciones. No muestra lo que te necesita *ahora*: la
reunión que empieza en diez minutos, la batería a punto de agotarse, la sesión de concentración que
te prometiste, cuánto de tu plan de IA ya se consumió hoy. Todo eso vive en rincones a los que hay
que ir a mirar, así que no miras, hasta que es tarde.

## Un caso, de principio a fin

Una reunión llega a diez minutos de distancia. Sin que toques nada:

| Antes | Qué ocurre | Después |
|---|---|---|
| ![El dock en su disposición de todos los días: aplicaciones ancladas, píldora de música, clima, calendario, widgets del sistema](docs/media/case-before.png) | La automatización de preparación de reunión adelanta el calendario, oculta música, clima y tiempo de pantalla, y el dock queda en una barra más tranquila | ![El dock en su disposición de reunión: el calendario al frente con la reunión visible, los widgets de ocio fuera](docs/media/case-after.png) |

Cada cambio de automatización es una **capa**: tu disposición guardada nunca se reescribe, la vista
de actividad muestra la regla, el motivo y el cambio en palabras llanas, y un clic lo deshace.
Quince minutos después de empezar la reunión, el dock devuelve todo a su lugar, en silencio.

## Qué hace

- **Aplicaciones**: anclar, agrupar en mosaicos-carpeta, vistas previas en vivo, globos de no leídos, reordenar arrastrando.
- **Widgets (14)**: reloj, calendario, música, temporizador de concentración, uso de IA con pronóstico de límites, y más.
- **Automatizaciones (9)**: reuniones, batería baja, disposición nocturna, concentración, pausas de pantalla; todas como capas reversibles.
- **Estilos (3)**: Glass, Liquid glass (el propio fondo de pantalla, curvado en el borde por una lente) y Oscuro.
- **Seguridad**: la barra de Windows se restaura primero y se oculta solo cuando el dock está verificado y listo; todo falla de forma visible.
- **Privacidad**: sin cuenta, sin telemetría; una sola consulta opcional del clima; todo lo demás queda en tu PC.

La lista completa, con todo el detalle, está en [docs/FEATURES.md](docs/FEATURES.md).

## Cómo se ve

Cada imagen se genera de forma determinista a partir de datos ficticios y pasa un escaneo de
privacidad antes de publicarse ([previews/privacy-scan.txt](previews/privacy-scan.txt)); ningún
dato real del usuario aparece en ninguna imagen.

![Glass, el estilo predeterminado: una barra de acrílico oscuro con aplicaciones ancladas a la izquierda y catorce widgets de muestra a la derecha](previews/dock-glass.png)
![Liquid glass: el estilo translúcido donde el propio fondo de pantalla, suavizado, es el material tras aplicaciones y widgets](previews/dock-clear.png)
![Oscuro: el estilo casi opaco del mismo dock](previews/dock-dark.png)

| Música | Uso de IA | Paleta de comandos |
| --- | --- | --- |
| ![La tarjeta de música: portada del álbum como fondo difuminado, barra de progreso en forma de onda y controles redondos, todo en el color de la portada](previews/flyout-media-glass.png) | ![El panel de uso de IA: límites de plan de Claude y Codex con porcentajes usados, cuentas regresivas y el pronóstico de ritmo](previews/flyout-quota-glass.png) | ![La paleta de comandos, primero el teclado, sobre aplicaciones, ventanas, ajustes y acciones de muestra](previews/command-palette.png) |

La galería completa (cada panel en cada estilo, menús y páginas de ajustes) está en
[previews/](previews/).

## La prueba, en palabras llanas

Este repositorio no es una demostración; es la fuente de una compilación que pasó una compuerta de
publicación antes de que nadie pudiera instalarla:

- **682 pruebas automatizadas**, ejecutadas sobre el árbol de desarrollo y sobre este árbol público exacto.
- **Compilaciones reproducibles**: tres publicaciones independientes desde un clon limpio de esta
  fuente producen el ejecutable publicado byte a byte.
- **Verificación presencial**: una compuerta operada por una persona a cinco escalas de pantalla
  (100 a 250 %), con pruebas de teclado y lector de pantalla, registrada con el hash de cada evidencia.
- **Auditorías independientes**: dos auditores de IA distintos revisaron a ciegas un defecto que
  bloqueaba la publicación, y sus documentos de trabajo reales están publicados, depurados de datos
  privados, en [docs/process/](docs/process/).

Cómo sale una versión: `trabajo → 682 pruebas → compilación reproducible → compuertas presenciales a
cinco escalas → auditorías independientes → visto bueno → instalación`.

## Descargar e instalar

La versión más reciente está en **[Releases](https://github.com/LeonelIvanCortesRodriguez/LucentDock/releases)**:
el ejecutable, su SHA-256 y `update.json`. Nada es silencioso: la instalación es por usuario
(`--install`), sin derechos de administrador, y la barra de Windows siempre sobrevive.

Las versiones son una **vista previa sin firmar**: SmartScreen avisará la primera vez ("Más
información → Ejecutar de todas formas"), y el SHA-256 junto a la descarga te permite verificar el
archivo tú mismo; [SECURITY.md](SECURITY.md) explica qué prueba eso y qué no.

## El papel de Leonel

LucentDock lo construye [Leonel Cortés](https://github.com/LeonelIvanCortesRodriguez), ejecutivo de
operaciones, trabajando como coordinador de sistemas de IA y no como programador de teclado: Claude
implementa y verifica, Codex implementa y audita por separado, y cada versión cruza una compuerta
presencial que solo Leonel puede operar. Los informes de auditoría en [docs/process/](docs/process/)
son los artefactos reales de ese flujo, incluida la regla de informar a los auditores a ciegas,
solo con los problemas y nunca con las hipótesis del constructor.

## La historia, en video

Un relato de ≈2 minutos de por qué existe LucentDock y cómo se verifica:
**[Español](docs/media/story-es.mp4)** · **[English](docs/media/story-en.mp4)**: con subtítulos y
póster tomado de la carátula.

## Compílalo y verifícalo tú mismo

Requiere el SDK de .NET 10.

```powershell
dotnet build src\Strata\Strata.csproj -c Release --no-incremental
dotnet test tests\Strata.Tests\Strata.Tests.csproj -c Release
dotnet publish src\Strata\Strata.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=none -o dist
```

Diseño y límites de fallo: [ARCHITECTURE.md](ARCHITECTURE.md). Inventario de datos y red:
[PRIVACY.md](PRIVACY.md). Cómo reportar un problema: [SECURITY.md](SECURITY.md). Licencia:
[MIT](LICENSE).

---

¹ El código, las pruebas y el ejecutable llevan el nombre clave interno del proyecto, **Strata**
(así la fuente publicada sigue siendo byte a byte la de la versión verificada); LucentDock es el
nombre público, y el renombre dentro del código llegará con una futura versión verificada.
