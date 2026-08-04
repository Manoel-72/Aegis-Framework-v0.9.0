-- Menu principal compatível: evita APIs opcionais no boot.
local title, subtitle, hint, bg, uiFont

local function tryLoadUIFont()
    if not (aegis.loadFont and aegis.setFont) then return end
    local candidates = {
        "fonts/Inter-Regular.ttf",
        "fonts/Roboto-Regular.ttf",
        "C:/Windows/Fonts/segoeui.ttf",
        "C:/Windows/Fonts/arial.ttf"
    }
    for _, p in ipairs(candidates) do
        local ok, f = pcall(aegis.loadFont, p, 28)
        if ok and f then uiFont = f; return end
    end
end

function aegis_init()
    aegis.log("[scene] menu init")
    if aegis.clearAll then aegis.clearAll() end
    tryLoadUIFont()
    GAME.score = GAME.score or 0
    GAME.lives = 3
    GAME.level = 1
    GAME.won = false

    bg = aegis.newRect(aegis.screenWidth(), aegis.screenHeight(), 0.08, 0.10, 0.16)
    aegis.setPosition(bg, 0, 0)
    aegis.setZ(bg, -10)
    title = aegis.newLabel("AEGIS DEMO PLATFORMER")
    subtitle = aegis.newLabel("Enter/Space/Start: jogar")
    hint = aegis.newLabel("Esc/Back: voltar ao menu")
    if uiFont then
        pcall(aegis.setFont, title, uiFont)
        pcall(aegis.setFont, subtitle, uiFont)
        pcall(aegis.setFont, hint, uiFont)
    end
    aegis.setColor(title, 0.65, 1.0, 0.72)
    aegis.setColor(subtitle, 0.8, 0.9, 0.84)
    aegis.setColor(hint, 0.75, 0.82, 0.8)

    aegis.setPosition(title, 430, 180)
    aegis.setPosition(subtitle, 430, 240)
    aegis.setPosition(hint, 430, 272)

    if aegis.tween then
        aegis.tween(title, { scaleX = 1.08, scaleY = 1.08 }, 0.7, "inout", nil, { loop = true, yoyo = true })
    end
end

function aegis_update(dt)
    if aegis.keyPressed("Enter") or aegis.keyPressed("Space") or aegis.padPressed(0, "Start") or aegis.padPressed(0, "A") then
        aegis.log("[scene] menu -> transitionTo(level1)")
        GAME.score = 0
        GAME.lives = 3
        GAME.level = 1
        if aegis.clearScreenShader then aegis.clearScreenShader() end
        aegis.transitionTo("level1", "fade", 0.35)
    end
end
function aegis_draw() end
