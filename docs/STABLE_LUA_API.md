# Aegis Stable Lua API

Nota: a lista congelada do MVP e a separacao oficial entre Stable, Legacy e Experimental
fica em `docs/MVP_API.md`.
O build Windows do MVP deve passar com `aegis build meu-jogo --target win-x64`.

Este documento define a API recomendada para novos jogos e templates da Aegis Engine.
As APIs antigas continuam funcionando por compatibilidade, mas novo código deve preferir
a superfície menor abaixo.

## Regra de Arquitetura

- `LuaRuntime` registra e orquestra a API Lua.
- Criação de componentes deve ficar em fábricas isoladas.
- O caminho oficial de criação é `aegis.create(tipo, opts)`.
- Remoção oficial é `aegis.destroy(obj)`.
- Caminhos de assets devem passar por `aegis.asset("pasta/arquivo.ext")`.
- Métodos antigos como `newSprite`, `newLabel`, `newRect` e `newProgressBar` são aliases legados.

## Assets Oficiais

Todos os caminhos de assets sao relativos a `res/`.

```lua
local playerPng = aegis.asset("sprites/player.png")
local jumpWav = aegis.asset("audio/jump.wav")

local ok = aegis.validateAssets()
if not ok then
  aegis.log("Projeto tem assets invalidos. Rode: aegis doctor .")
end
```

APIs estaveis:

- `aegis.asset(path)`: valida que o arquivo existe em `res/` e retorna o caminho normalizado.
- `aegis.assetExists(path)`: retorna `true/false` sem lancar erro.
- `aegis.validateAssets()`: roda o validador do projeto atual e registra erros/warnings no log.
- `aegis.assetReport()`: retorna tabela Lua com `ok`, `errors`, `warnings` e `issues`.

Use `aegis.asset(...)` em sprites, atlas, tilemaps e sons para descobrir erros cedo.

## Criação Recomendada

Labels usam uma fonte padrao automatica. O jogo pode fornecer uma fonte melhor em
`res/fonts/Inter-Regular.ttf`, `res/fonts/NotoSans-Regular.ttf` ou carregar uma fonte
manual com `aegis.loadFont`.
Para texto grande, prefira `aegis.create("label", { size = 30 })` ou
`aegis.newLabelSize(text, 30)` em vez de ampliar com `setScale`, porque escalar
texto rasterizado deixa a fonte pixelada.

```lua
local player = aegis.create("sprite", {
  path = aegis.asset("sprites/player.png"),
  x = 100,
  y = 120,
  pivotX = 0.5,
  pivotY = 0.5
})

local title = aegis.create("label", {
  text = "Novo Jogo",
  x = 400,
  y = 80,
  r = 1,
  g = 1,
  b = 1,
  pivotX = 0.5
})

local hp = aegis.create("progressBar", {
  x = 24,
  y = 24,
  width = 220,
  height = 16,
  bg = { r = 0.12, g = 0.12, b = 0.14 },
  fill = { r = 0.2, g = 0.9, b = 0.35 }
})
```

## Tipos Suportados

- `group`: objeto vazio para agrupar filhos.
- `sprite`: sprite por textura. Requer `path`.
- `rect`: retangulo colorido. Usa `width`/`height` ou `w`/`h`.
- `label`: texto simples. Usa `text`.
- `richLabel`: texto com markup. Usa `text` ou `markup`.
- `panel`: painel 9-slice. Requer `path`; aceita `border`, `width`, `height`.
- `flow`: container horizontal/vertical. Aceita `direction`, `gap`, `padding`, `align`.
- `progressBar`: barra de progresso. Aceita `width`, `height`, `bg`, `fill`.
- `anim` ou `animatedSprite`: spritesheet animado. Requer `path`, `frameWidth`/`frameHeight`.

## Opções Comuns

Todos os tipos aceitam:

```lua
{
  x = 0,
  y = 0,
  z = 0,
  scale = 1,
  scaleX = 1,
  scaleY = 1,
  rotation = 0,
  alpha = 1,
  visible = true,
  pivotX = 0,
  pivotY = 0
}
```

Para cores, use `r`, `g`, `b`, `a` com valores entre `0` e `1`.

## Animacao Recomendada

Use `newAnimator` para spritesheet em grade e `newAtlasAnimator` para atlas
Aseprite JSON.

```lua
local sprite = aegis.create("sprite", { path = aegis.asset("sprites/player.png") })
local anim = aegis.newAnimator(sprite, 32, 32)

aegis.addClip(anim, "idle", { 0, 1, 2, 3 }, 5, true)
aegis.addClip(anim, "attack", { 4, 5, 6 }, 10, false)
aegis.play(anim, "idle")

aegis.onAnimEnd(anim, function(a, clip)
  if clip == "attack" then
    aegis.play(a, "idle")
  end
end)
```

APIs uteis:

- `aegis.animFinished(anim)`
- `aegis.isAnimFinished(anim)`
- `aegis.currentClip(anim)`
- `aegis.setFlip(sprite, flipX, flipY?)`
- `aegis.setAnimFlip(sprite, flipX, flipY?)`

## Cenas E Dados

```lua
aegis.registerScene("gameover", "scenes/gameover.lua")
aegis.transitionTo("gameover", "fade", 0.35, { score = 120 })
aegis.pushScene("pause", { from = "level1" })
aegis.popScene()

aegis.onSceneEnter(function(scene, data)
  if data then aegis.log("score: " .. tostring(data.score)) end
end)

aegis.onSceneExit(function(scene, nextScene, data)
  aegis.log(scene .. " -> " .. nextScene)
end)
```

Modos de transicao:

- `fade`
- `none`
- `slide`

Use `pushScene/popScene` para pause menu, inventario e overlays. A cena
empilhada nao destruiu o mundo; ao voltar, a engine remove os objetos criados
pela cena empilhada e restaura os callbacks anteriores.

## Tilemap

`aegis.setTile` e `aegis.getTile` aceitam indice numerico ou nome da layer:

```lua
aegis.setTile(map, 0, 10, 8, 2)
aegis.setTile(map, "Ground", 10, 8, 2)
```

## Como Evoluir

Para adicionar um novo componente:

1. Crie ou ajuste a classe do componente em `src/Aegis/Display`, `src/Aegis/Physics` ou domínio correto.
2. Adicione a criação em `src/Aegis/Scripting/Components/ComponentFactory.cs`.
3. Exponha via `aegis.create("novoTipo", opts)`.
4. Só adicione alias em `LuaRuntime` se for necessário para compatibilidade.

O objetivo é manter uma API pequena para jogos, sem transformar `LuaRuntime` em um ponto único de toda regra de criação.
