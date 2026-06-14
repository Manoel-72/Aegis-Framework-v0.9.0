# Aegis Engine MVP API

Este documento congela a superficie recomendada para o MVP da Aegis Engine.
O objetivo e reduzir instabilidade, proteger jogos existentes e dar um contrato claro
para templates, exemplos e IA.

## Status de API

- `Stable/MVP`: pode ser usado em jogos e templates oficiais.
- `Legacy`: mantido para compatibilidade, mas nao deve aparecer em codigo novo.
- `Experimental`: pode mudar ou ser removido antes do MVP. A engine emite aviso no log quando usado.

## Stable/MVP

### Ciclo de vida

- `aegis_init`
- `aegis_update(dt)`
- `aegis_draw`
- `aegis_draw_ui`

### Assets

Todos os caminhos sao relativos a `res/`. Codigo novo deve validar assets cedo:

```lua
local playerPng = aegis.asset("sprites/player.png")
local ok = aegis.validateAssets()
```

- `aegis.asset(path)`: valida existencia e retorna caminho normalizado.
- `aegis.assetExists(path)`: consulta segura sem exception.
- `aegis.validateAssets()`: roda o `AssetValidator` no projeto atual.
- `aegis.assetReport()`: retorna `{ ok, errors, warnings, issues }`.

### Erros Lua

Erros de script sao embrulhados pela engine com:

- fase (`boot`, `aegis_init`, `aegis_update`, `aegis_draw`, cena ou script de entidade);
- arquivo;
- mensagem original do Lua;
- dica curta de correcao.

Isso nao substitui testes, mas melhora a primeira experiencia e o `crash.log`.

### Cena

- `aegis.registerScene(name, file)`
- `aegis.transitionTo(name, mode?, seconds?, data?)`
- `aegis.pushScene(name, data?)`
- `aegis.popScene()`
- `aegis.sceneData()`
- `aegis.onSceneEnter(callback)`
- `aegis.onSceneExit(callback)`
- `aegis.clearAll`
- `aegis.uiClear`
- `aegis.worldClear`
- `aegis.destroy(obj)`

Exemplo de dados entre cenas:

```lua
aegis.transitionTo("gameover", "fade", 0.35, { score = 120 })

aegis.onSceneEnter(function(scene, data)
    if data then aegis.log("score: " .. tostring(data.score)) end
end)
```

`mode` aceita `fade`, `none` e `slide`.

Para pause menu, inventario e modais, use `pushScene`:

```lua
aegis.pushScene("pause", { from = "level1" })

-- dentro da cena pause:
function aegis_update(dt)
    if aegis.keyPressed("Escape") then
        aegis.popScene()
    end
end
```

`pushScene` preserva o mundo atual e carrega uma cena por cima. `popScene`
remove os objetos criados pelo overlay e restaura os callbacks Lua anteriores.

### Camada UI / HUD (MVP)

A engine mantem duas raizes de cena:

- **Mundo (`S2D`)**: desenhada em `aegis_draw`, afetada pela camera quando ativa.
- **UI (`Ui2D`)**: desenhada automaticamente em `aegis_draw_ui`, sempre em espaco de tela.

Para criar HUD, menus e overlays fixos na tela:

1. **Widgets declarativos** (recomendado): `hud = true` ou `layer = "ui"` em `aegis.create`, ou ultimo argumento `true` em `aegis.newLabel`, `aegis.newFlow`, `aegis.newRect`, etc.
2. **Primitivas imediatas**: `aegis.drawText`, `aegis.drawRect` dentro de `aegis_draw_ui()` (suportam alpha opcional).

Exemplo:

```lua
local hud = aegis.newFlow("vertical", { gap = 6, padding = 12, hud = true })
local score = aegis.newLabelSize("Score 0", 18, true)
aegis.flowAdd(hud, score)
aegis.setPosition(hud, 16, 16)

function aegis_draw_ui()
    -- opcional: overlays imediatos
    aegis.drawRect(0, 0, 4, aegis.screenHeight(), 0, 0, 0, 0.25)
end
```

- `aegis.uiClear()` remove apenas objetos da camada UI (util ao trocar overlay sem resetar o mundo).
- `aegis.clearAll()` limpa mundo **e** UI.
- Botoes (`aegis.newButton`) em objetos UI usam coordenadas de tela; em objetos de mundo a engine converte via camera.
- `aegis.floatText(x, y, text, { hud = true })` ou `{ screen = true }` para numeros flutuantes fixos na tela.

### Criacao de componentes

- `aegis.create(kind, opts)`

Tipos estaveis para `aegis.create`:

- `group`
- `sprite`
- `rect`
- `label`
- `richLabel`
- `panel`
- `flow`
- `progressBar`
- `anim`
- `animatedSprite`

### Transform

- `aegis.setPosition(obj, x, y)`
- `aegis.setPositionNorm(obj, nx, ny)`
- `aegis.move(obj, dx, dy)`
- `aegis.setScale(obj, sx, sy)`
- `aegis.setRotation(obj, radians)`
- `aegis.setAlpha(obj, alpha)`
- `aegis.setVisible(obj, visible)`
- `aegis.setFlip(bitmap, flipX, flipY?)`
- `aegis.setAnimFlip(bitmap, flipX, flipY?)`
- `aegis.setPivot(bitmap, px, py)`
- `aegis.setZ(obj, z)`
- `aegis.getZ(obj)`
- `aegis.getX(obj)`
- `aegis.getY(obj)`
- `aegis.getWidth(bitmap)`
- `aegis.getHeight(bitmap)`
- `aegis.centerX(obj)`

### Texto e fontes

- Fonte padrao automatica: `aegis.create("label", ...)`, `aegis.newLabel` e `aegis.drawText`
  devem funcionar sem o jogo carregar fonte manualmente.
- A engine procura primeiro em `res/fonts` e depois nas fontes do sistema.
- Fonte recomendada para jogos: `res/fonts/Inter-Regular.ttf` ou `NotoSans-Regular.ttf`.
- Para texto grande, use fonte no tamanho real (`size` ou `newLabelSize`) em vez de escalar label.

- `aegis.setText(label, text)`
- `aegis.setColor(label, r, g, b, a)`
- `aegis.loadFont(path, size)`
- `aegis.loadDefaultFont(size)`
- `aegis.newLabel(text, hud)` — ultimo parametro opcional: `true` = camada UI
- `aegis.newLabelSize(text, size, hud)`

### Animacao

- `aegis.newAnimator(sprite, frameWidth, frameHeight)` para spritesheet em grade.
- `aegis.newAtlasAnimator(sprite, atlas)` para atlas Aseprite JSON.
- `aegis.addClip(anim, name, frames, fps, loop?)`
- `aegis.addAtlasClip(anim, name, frameNames, fps, loop?)`
- `aegis.play(anim, name, restart?)`
- `aegis.stopAnimator(anim)`
- `aegis.currentClip(anim)`
- `aegis.animFinished(anim)`
- `aegis.isAnimFinished(anim)`
- `aegis.onAnimEnd(anim, callback)`

Exemplo:

```lua
aegis.addAtlasClip(anim, "attack", { "attack_00", "attack_01", "attack_02" }, 10, false)
aegis.onAnimEnd(anim, function(a, clip)
    if clip == "attack" then attacking = false end
end)
```
- `aegis.drawText(text, x, y, r, g, b, a)` — use em `aegis_draw_ui`
- `aegis.drawRect(x, y, w, h, r, g, b, a)` — use em `aegis_draw_ui`
- `aegis.newRichLabelSize(markup, size)`
- `aegis.setFont(label, font)`
- `aegis.setMarkup(richLabel, markup)`
- `aegis.setFontRich(richLabel, font)`
- `aegis.setPivotRich(richLabel, px, py)`

### Input

- `aegis.keyDown(key)`
- `aegis.keyPressed(key)`
- `aegis.mouseX`
- `aegis.mouseY`
- `aegis.mouseLeft`
- `aegis.mouseLeftJust`
- `aegis.mouseRight`
- `aegis.mouseRightJust`
- `aegis.mouseScroll`
- `aegis.padConnected`
- `aegis.padDown`
- `aegis.padPressed`
- `aegis.padAxis`
- `aegis.padVibrate`

### Tela e camera basica

- `aegis.screenWidth`
- `aegis.screenHeight`
- `aegis.setCameraTarget`
- `aegis.setCameraOff`
- `aegis.setCameraZoom`
- `aegis.setCameraOffset`
- `aegis.setCameraLimits`
- `aegis.cameraX`
- `aegis.cameraY`
- `aegis.screenToWorldX`
- `aegis.screenToWorldY`
- `aegis.screenShake`

### Audio

- `aegis.playSound`
- `aegis.playSoundEx`
- `aegis.playSoundAt`
- `aegis.playMusic`
- `aegis.playMusicLooped`
- `aegis.stopMusic`
- `aegis.pauseMusic`
- `aegis.resumeMusic`
- `aegis.setSfxVolume`
- `aegis.setMusicVolume`
- `aegis.setGroupVolume`
- `aegis.crossfadeTo`
- `aegis.musicPlaying`

### Fisica basica

- `aegis.addCollider`
- `aegis.addCircleCollider`
- `aegis.removeCollider`
- `aegis.setColliderLayer`
- `aegis.setColliderMask`
- `aegis.setTrigger`
- `aegis.setColliderOffset`
- `aegis.getColliderOf`
- `aegis.onCollide`
- `aegis.onCollideEnter`
- `aegis.onCollideExit`
- `aegis.addRigidbody`
- `aegis.removeRigidbody`
- `aegis.setVelocity`
- `aegis.setVelocityX`
- `aegis.addImpulseY`
- `aegis.setVel`
- `aegis.setVelX`
- `aegis.jumpY`
- `aegis.addVelocity`
- `aegis.getVelocityX`
- `aegis.getVelocityY`
- `aegis.setGravity`
- `aegis.setGroundFriction`
- `aegis.setGlobalGravity`
- `aegis.setKinematic`
- `aegis.isGrounded`
- `aegis.raycast`
- `aegis.raycastMask`
- `aegis.lineOfSight`
- `aegis.overlapCircle`
- `aegis.overlapRect`

### Save/config

- `aegis.save`
- `aegis.load`
- `aegis.loadConfig`
- `aegis.setDisplayMode`
- `aegis.setFullscreen`
- `aegis.setResolution`

Modos de display estaveis no `aegis.cfg`:

- `"displayMode": "windowed"`: janela normal.
- `"displayMode": "borderless"`: tela cheia sem borda, recomendado para MVP.

`fullscreen` continua existindo por compatibilidade. Codigo novo deve preferir
`displayMode`, porque evita fullscreen exclusivo e reduz risco de tela preta em
drivers/SDL/MonoGame.

### Tween e particulas simples

- `aegis.tween`
- `aegis.newSequence`
- `aegis.seqAdd`
- `aegis.seqWait`
- `aegis.seqPlay`
- `aegis.seqStop`
- `aegis.burst`
- `aegis.newEmitter`
- `aegis.stopEmitter`

### Build Windows

Para o MVP, a validacao oficial e:

```powershell
dotnet build src\Aegis.CLI\Aegis.CLI.csproj --no-restore
```

O contrato de exportacao Windows do MVP e:

```powershell
aegis build meu-jogo --target win-x64
```

Saida esperada:

```text
dist/<nome-do-jogo>-win-x64/
dist/<nome-do-jogo>-win-x64.zip
```

O build deve conter:

- `aegis-cli.exe`
- `main.lua`
- `aegis.toml`
- `res/`
- `JOGAR.bat`
- `README-RUN.txt`
- `aegis-build.json`

Builds paralelos de `Aegis` e `Aegis.CLI` devem ser evitados porque os dois podem
tentar gravar/copiar o mesmo `Aegis.dll` ao mesmo tempo.

## Legacy

Estas APIs continuam funcionando, mas codigo novo deve preferir `aegis.create` e `aegis.destroy`:

- `aegis.newSprite`
- `aegis.newRect`
- `aegis.removeObject`
- `aegis.newLabel`
- `aegis.newAnim`
- `aegis.newRichLabel`
- `aegis.newPanel`
- `aegis.newFlow`
- `aegis.newProgressBar`
- `aegis.setZOrder`
- `aegis.getZOrder`

## Experimental

Estas APIs nao fazem parte do contrato MVP. Elas podem existir no runtime, mas nao devem ser usadas por templates oficiais:

- Drag/drop:
  `aegis.newDraggable`, `aegis.onDragStart`, `aegis.onDragMove`, `aegis.onDragEnd`, `aegis.getDragTarget`
- Hand/card layout:
  `aegis.newHand`, `aegis.handAdd`, `aegis.handRemove`, `aegis.handLayout`, `aegis.handSetHover`
- Upgrade system:
  `aegis.addUpgrade`, `aegis.onUpgradeChosen`, `aegis.getUpgradeLevel`, `aegis.showUpgrades`, `aegis.hideUpgrades`
- Camera autozoom:
  `aegis.setCameraAutozoom`
- Audio 3D:
  `aegis.playSoundAt3D`
- Fisica ainda nao congelada:
  `aegis.addSlopeCollider`, `aegis.setSlopeDir`, `aegis.setOneWay`
- Editor pipe:
  `Aegis.Editor.EditorPipeHost` e comunicacao por named pipe com o editor.

## Regra Para Templates

Templates novos devem usar apenas APIs `Stable/MVP`.
Se um template precisar de API experimental, isso deve estar declarado no README do template.

## Regra Para Evolucao

1. Recurso novo nasce como `Experimental`.
2. Precisa de exemplo, documentacao e build verde para virar `Stable/MVP`.
3. API antiga que foi substituida vira `Legacy`.
4. Nada experimental deve ser requisito para build, templates oficiais ou documentacao principal do MVP.
