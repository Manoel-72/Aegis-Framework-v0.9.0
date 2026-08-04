# Aegis Demo Platformer

Demo vertical slice de 3 fases para provar os sistemas principais da Aegis Engine.

## Sistemas usados

- Player com run/jump e animação por atlas Aseprite JSON.
- Tilemap Tiled JSON com colisão automática e merge de retângulos.
- Inimigos com pathfinding A* em `NavGrid`.
- Coletáveis usando circle collider como trigger.
- HUD com `FlowContainer`.
- Shader outline no player e screen shaders no menu/game over.
- Partículas no pulo, coleta e morte.
- Áudio de pulo/coleta/dano e som posicional dos inimigos.
- Menu → jogo → game over → menu via SceneManager.
- Teclado e gamepad.

## Rodar

```bash
aegis run demo-platformer
```

## Controles

- A/D ou setas: mover
- Espaço/Seta cima/A do controle: pular
- Enter/Start: confirmar
- Esc/Back: voltar ao menu

## Observação

Os assets são placeholders gerados para o projeto. Troque por assets finais depois.
