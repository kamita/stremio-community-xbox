# Auditoría de cambios — Stremio Community Xbox

- **Fecha**: 2026-11-03
- **Rango auditado**: `4f68fef..HEAD` (commits `51098c5` + `3bf9493`)
- **Estado**: compila sin errores; working tree limpio (excepto `.vscode/`, `StremioCommunity/temp_cert.cer`, `scripts/InspectWinmd/`)

## Ámbitos verificados (correctos)

1. **Cursor delta-time** (`OnPadPolled`): movimiento integrado con tiempo real (dt clamped 1–100 ms), 1500 DIP/s a fondo; semántica de `active` preservada.
2. **Persistencia de sesión**: quitado el force-enable del cursor en `OnLoaded`; `SaveSetting` añadido en los 5 sitios de toggle (Y/RB/Menu/LB en debug + `AdjustSetting`).
3. **Idioma preferido**: `LanguageCodes`/`LanguageLabels` 1:1 (12 entradas), clamping correcto, filas `audioLang`/`subLang` en Settings, `ApplyPreferredLanguageTracks` con 2 pases (StartsWith → Contains), orden de llamadas correcto en `OnMediaOpened`.
4. **Overlay de ayuda**: XAML correcto (Grid 2 col, `HelpLeft`/`HelpRight`); se cierra con cualquier botón (HandlePlayerButton, OnBackRequested); View en player lo abre y no toca el debug panel.
5. **D-pad L/R en menú de subtítulos**: índice `_subtitleTrackCount` consistente; `RebuildSubtitleValueRows` escribe en las filas correctas; wrap-around correcto.
6. **Diagnostics 8BitDo**: throttle 800 ms por (vk, scan); log `ACCEL key vk=sc=ext=`; política de cursor: flechas siempre bloqueadas, teclado visible → Windows entrega, resto bloqueado.
7. **Timing/speed de subtítulos**: logging completo (sin pistas / API ausente / error); re-presentación best-effort del track activo; filas eliminadas del panel de ajustes (ahora solo en el menú de subtítulos).
8. **CHANGELOG y todo.md**: consistentes con lo implementado.

## Hallazgos (no corregidos, en orden de relevancia)

### H1 — Stepping con A es unidireccional
`SelectSubtitleMenu` con **A** sobre la fila de delay/speed **solo avanza** (`+0.5s` / `+0.05x` con wrap). Para bajar hay que usar **D-pad Izquierda**. Consistente con el diseño (A = avance, D-pad = ajuste fino), pero si se esperaba que A también bajara, no lo hace.

### H2 — Wrap "salto al extremo opuesto"
Al rebasar el límite, el valor salta al otro extremo (delay `+30 → -30`, rate `2.0 → 0.5`) en lugar de continuar o mantener. Con `dir=-1` el wrap también salta (`-30 → 30`, `0.5 → 2.0`). Funcional, pero el comportamiento es "teletransporte" y no "envolver en la misma dirección".

### H3 — `Environment.TickCount` (int) puede envolver
`TickCount` wrappea cada ~24.9 días de uptime. En un wrap justo, `dtTicks` saldría enorme → clamped a 100 ms → el cursor daría un "salto" de ~150 DIP en un solo tick. Probabilidad muy baja; sin impacto práctico.

### H4 — `_accelKeyLastLog` sin límite de entradas
`Dictionary<string,int>` que crece sinbound si un mando produce muchas combinaciones (vk, scan) distintas. En la práctica (mando Xbox + 8BitDo) son decenas; no es un bug.

### H5 — Campo muerto `_audioTrackCount`
Se asigna en `OpenAudioMenu` pero ya no se usa en ninguna lógica (el menú de audio no tiene filas ajustables desde que se eliminó la fila de volumen). Código inerte; puede eliminarse.

### H6 — `OpenHelpOverlay` no cierra menús abiertos
Si `AudioMenu`/`SubtitleMenu` estuvieran abiertos, Help se superpondría sin cerrarlos. Hoy no es alcanzable (View no abre menús), pero quedaría latente si el flujo de View cambia en el futuro.

### H7 — Coste trivial de `OpenHelpOverlay`
Genera 20 `TextBlock` + 10 `Grid` en cada apertura. Negligible (se abre raramente); no hay pooling ni cacheo.

### H8 — Nota de entorno: archivos sin versionar en el repo
- `StremioCommunity/temp_cert.cer` — exportación temporal del certificado de firma; **candidata para borrar** o añadir a `.gitignore`.
- `scripts/InspectWinmd/` — proyecto de inspección temporal; borrar o ignorar.
- `.vscode/` — configuración local de editor; ignorar.

## Veredicto

Ningún hallazgo es un bug de correctness en el flujo actual. H1/H2 son decisiones de UX que convendría confirmar; H5 es limpieza pendiente; H8 es higiene de repo. El resto (H3–H4, H6–H7) son observaciones defensivas de baja probabilidad.
