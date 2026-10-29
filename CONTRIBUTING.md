# Cómo contribuir · Contributing

[Español](#español) · [English](#english)

## Español

¡Gracias por querer sumar! WildTrack es un proyecto **gratuito y sin fines de lucro**: lo
hacemos para la comunidad, y las contribuciones son bienvenidas. Todo cambio pasa por la **aprobación del
mantenedor** antes de entrar al proyecto.

### Cómo se hace

1. **Abrí un issue primero** (un bug, una idea o algo de [PENDIENTES.md](PENDIENTES.md) que quieras
   encarar), para que nadie trabaje en vano. Para cambios chicos (un error de tipeo, un bug evidente) podés
   ir directo al paso 2.
2. Hacé un **fork**, creá una rama a partir de `master` y hacé tus cambios.
3. Abrí un **Pull Request** contando qué cambia y por qué, y cómo lo probaste. Se usa la plantilla que aparece sola.
4. El mantenedor lo revisa. Puede pedirte cambios. **Nada entra a `master` sin su aprobación.**

### Antes de abrir el PR

- `dotnet build` desde la raíz (`WildTrack.slnx`) tiene que compilar **sin warnings nuevos**.
- Probá el motor y el proyecto con `wtlab` y la ventana con `WildTrack.exe`. Si no tenés interfaz ASIO o
  cámara, decí en el PR qué no pudiste probar.
- Un PR, una idea: cambios chicos y enfocados se revisan (y se aprueban) mucho más rápido.
- No subas archivos de audio, video ni binarios (`*.wav`, `bin/`, `obj/`).

### Reglas del código

La guía completa está en [AGENTS.md](AGENTS.md) (y, para la ventana, en [src/WildTrack.App/AGENTS.md](src/WildTrack.App/AGENTS.md) y [docs/interfaz.md](docs/interfaz.md)); lo esencial:

- **Audio en tiempo real**: dentro del callback de ASIO no se asigna memoria, no se bloquea, no hay locks ni
  disco ni UI.
- **Capas**: `App → Projects → Audio`; `Audio` y `Projects` no conocen la interfaz.
- **Idioma**: la interfaz está en español rioplatense (con voseo) y todo texto nuevo lleva su clave en
  `Assets/Lang/*.json`. Identificadores y comentarios del código, en inglés.
- **Estilo**: escribí como el código que ya está (clases `sealed`, `<summary>` en cada clase, comentarios
  solo para el *por qué*).
- **Formato del proyecto**: lo que se guarda en `guitarcam.json` es compatible hacia atrás.
- Sin dependencias nuevas de NuGet sin charlarlo antes.

### Licencia de tus aportes

Al contribuir aceptás que tu aporte se publique bajo la misma licencia del proyecto,
[GPL-3.0](LICENSE). No hace falta firmar nada más.

### Uso de IA

Se puede usar IA para programar, pero **vos sos responsable de lo que enviás**: revisalo, probalo y
aclará en el PR si una parte importante salió de una herramienta.

## English

Thanks for wanting to help! WildTrack is a **free, non-profit** project made for the
community, and contributions are welcome. Every change goes through the **maintainer's approval** before it
is merged.

1. **Open an issue first** (a bug, an idea, or something from [PENDIENTES.md](PENDIENTES.md), which is in
   Spanish) so nobody works in vain. Tiny fixes can go straight to a pull request.
2. **Fork** the repo, branch from `master`, and make your changes.
3. Open a **Pull Request** explaining what changes, why, and how you tested it.
4. The maintainer reviews it and may ask for changes. **Nothing is merged into `master` without their approval.**

Before opening the PR: `dotnet build` must pass **with no new warnings**; test with `wtlab` and the app (say
what you could not test if you lack an ASIO interface or a camera); keep PRs small and focused; do not commit
audio, video or binaries.

Code rules (full guide in [AGENTS.md](AGENTS.md) and, for the window, [src/WildTrack.App/AGENTS.md](src/WildTrack.App/AGENTS.md); in Spanish): no allocations, locks, disk or UI inside the
real-time audio callback; dependencies point down (`App → Projects → Audio`); UI text is in Spanish and every
new string needs a key in every `Assets/Lang/*.json`; code identifiers and comments are in English; no new
NuGet packages without discussing them first.

By contributing you agree that your work is released under the project's license, [GPL-3.0](LICENSE). AI
tools are fine, but you are responsible for what you submit: review it, test it, and say in the PR if a
significant part came from a tool.
