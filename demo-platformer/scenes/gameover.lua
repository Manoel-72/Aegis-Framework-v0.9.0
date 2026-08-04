local title, info, bg, uiFont

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
    aegis.log("[scene] gameover init")
    if aegis.clearAll then aegis.clearAll() end
    tryLoadUIFont()
    bg = aegis.newRect(aegis.screenWidth(), aegis.screenHeight(), 0.14, 0.06, 0.08)
    aegis.setPosition(bg, 0, 0)
    aegis.setZ(bg, -10)
    local msg = GAME.won and "VOCÊ VENCEU" or "GAME OVER"
    title = aegis.newLabel(msg)
    info = aegis.newLabel("Score: " .. tostring(GAME.score or 0) .. "  |  Enter/Start para voltar")
    if uiFont then
        pcall(aegis.setFont, title, uiFont)
        pcall(aegis.setFont, info, uiFont)
    end
    aegis.setColor(title, 1.0, 0.35, 0.35)
    aegis.setPosition(title, 500, 220)
    aegis.setPosition(info, 350, 270)
    aegis.burst(aegis.screenWidth()/2, aegis.screenHeight()/2, { count=45, speed=120, life=0.8, size=4, r=1, g=0.2, b=0.2 })
end
function aegis_update(dt)
    if aegis.keyPressed("Enter") or aegis.padPressed(0, "Start") then
        if aegis.clearScreenShader then aegis.clearScreenShader() end
        aegis.log("[scene] gameover -> transitionTo(menu)")
        aegis.transitionTo("menu", "fade", 0.35)
    end
end
function aegis_draw() end
