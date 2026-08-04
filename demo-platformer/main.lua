-- Aegis demo platformer: registra cenas e abre menu.
GAME = GAME or { score = 0, lives = 3, level = 1, maxLevel = 3 }
local _booted = false

function aegis_init()
    aegis.log("[scene] boot main.lua init")
    aegis.registerScene("menu", "scenes/menu.lua")
    aegis.registerScene("level1", "scenes/level.lua")
    aegis.registerScene("level2", "scenes/level.lua")
    aegis.registerScene("level3", "scenes/level.lua")
    aegis.registerScene("gameover", "scenes/gameover.lua")
    _booted = false
end

function aegis_update(dt)
    if not _booted then
        _booted = true
        aegis.log("[scene] main -> transitionTo(menu)")
        aegis.transitionTo("menu", "none", 0.01)
    end
end
function aegis_draw() end
