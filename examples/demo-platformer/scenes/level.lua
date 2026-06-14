-- Cena única para 3 fases. Usa tilemap, colisão automática, player, inimigos, coletáveis, HUD e câmera.
local map, nav, player, rb, playerCol, atlas, anim
local playerFacing = 1
local enemies, coins, goal, goalLock, checkpoint = {}, {}, nil, nil, nil
local hud = { lives = 3, score = 0, coins = 0, coinMax = 5, requiredCoins = 3 }
local tutorial = { active = false, alpha = 0, pulse = 0 }
local collectedCoins = {}
local pendingRemoveCoins = {}
local checkpointActive = false
local checkpointX, checkpointY = 48, 380
local levelMessage = ""
local levelMessageTimer = 0
local exitWasUnlocked = false
local levelComplete = false
local sceneChanging = false
local hurtCooldown = 0
local coyote = 0
local jumpBuffer = 0
local jumpHeldPrev = false
local airJumpUsed = false
local fallCooldown = 0
local jumpCooldown = 0
local jumpBoostFrames = 0
local MOVE_SPEED = 220
local GROUND_ACCEL = 1550
local AIR_ACCEL = 760
local GROUND_DECEL = 2050
local AIR_DECEL = 520
local ASSETS = {
    heart = aegis.asset("sprites/heart.png"),
    coin = aegis.asset("sprites/coin.png"),
    enemy = aegis.asset("sprites/enemy.png"),
    playerSheet = aegis.asset("sprites/player-sheet.png"),
    playerAtlas = aegis.asset("sprites/player-sheet.json"),
    sfxCoin = aegis.asset("audio/coin.wav"),
    sfxHurt = aegis.asset("audio/hurt.wav"),
    sfxDeath = aegis.asset("audio/death.wav"),
    sfxLand = aegis.asset("audio/land.wav"),
    sfxJump = aegis.asset("audio/jump.wav"),
    sfxEnemy = aegis.asset("audio/enemy.wav"),
    music = aegis.asset("audio/music.wav"),
}

local function approach(current, target, amount)
    if current < target then
        return math.min(current + amount, target)
    end
    if current > target then
        return math.max(current - amount, target)
    end
    return target
end

local function syncPlayerFacing()
    if player and aegis.setFlip then
        aegis.setFlip(player, playerFacing < 0)
    end
end

local function levelIndex()
    return math.max(1, math.min(GAME.maxLevel or 9, GAME.level or 1))
end

local function mapIndex()
    return ((levelIndex() - 1) % 3) + 1
end

local function goToScene(scene, duration)
    if sceneChanging then return end
    sceneChanging = true
    aegis.transitionTo(scene, "fade", duration or 0.35)
end

local function syncHud()
    hud.lives = GAME.lives or 3
    hud.score = GAME.score or 0
end

local function buildHud()
    hud.coins = 0
    hud.coinMax = 5 + math.min(5, math.floor((levelIndex() - 1) / 2))
    hud.requiredCoins = math.min(hud.coinMax, 3 + math.floor((levelIndex() + 1) / 2))
    syncHud()
end

local function goalUnlocked()
    return hud.coins >= hud.requiredCoins
end

local function showLevelMessage(text, seconds)
    levelMessage = text
    levelMessageTimer = seconds or 1.6
end

local function updateGoalState()
    if goalLock then
        aegis.setAlpha(goalLock, goalUnlocked() and 0.0 or 0.72)
    end
    if goalUnlocked() and not exitWasUnlocked then
        exitWasUnlocked = true
        showLevelMessage("Porta liberada!", 1.4)
        if goal then
            aegis.burst(aegis.getX(goal) + 18, aegis.getY(goal) + 36, {
                count = 28, speed = 90, life = 0.45, size = 3,
                r = 0.45, g = 1.0, b = 0.65
            })
        end
    end
end

local function respawnPlayer()
    aegis.setPosition(player, checkpointX, checkpointY)
    aegis.setVelocity(rb, 0, 0)
    coyote = 0.12
    jumpBuffer = 0
    airJumpUsed = false
end

local function drawHudChrome()
    local sw = aegis.screenWidth()
    local barH = 62
    local iconY = 20
    local heartScale = 1.45
    local coinScale = 1.5

    aegis.drawRect(0, 0, sw, barH + 4, 0.03, 0.05, 0.09, 0.82)
    aegis.drawRect(0, barH, sw, 2, 0.35, 0.75, 0.55, 0.65)

    -- Esquerda: fase + vidas
    aegis.drawRect(14, 14, 78, 32, 0.10, 0.28, 0.22, 0.92)
    aegis.drawRect(14, 14, 78, 3, 0.40, 0.82, 0.58, 1)
    aegis.drawText("FASE", 26, 18, 0.45, 0.78, 0.68, 0.9)
    aegis.drawText(tostring(levelIndex()) .. "/" .. tostring(GAME.maxLevel or 9), 26, 32, 0.20, 1.0, 0.78)

    local heartStartX = 108
    local heartGap = 30
    for i = 1, 3 do
        local hx = heartStartX + (i - 1) * heartGap
        local alpha = i <= hud.lives and 1.0 or 0.28
        aegis.drawSprite(ASSETS.heart, hx, iconY, heartScale, 1, 1, 1, alpha)
    end

    -- Direita superior: moedas
    local coinIconX = sw - 72
    local coinText = tostring(hud.coins) .. "/" .. tostring(hud.coinMax)
    aegis.drawRect(coinIconX - 10, 12, 88, 34, 0.08, 0.09, 0.12, 0.75)
    aegis.drawSprite(ASSETS.coin, coinIconX, iconY, coinScale, 1, 1, 1, 1)
    aegis.drawText(coinText, coinIconX + 28, iconY + 5, 1.0, 0.88, 0.35)

    -- Score abaixo das moedas (direita)
    local scoreStr = tostring(hud.score)
    local scoreX = sw - 90
    aegis.drawText("SCORE", scoreX, 48, 0.5, 0.72, 0.55, 0.75)
    aegis.drawText(scoreStr, scoreX + 2, 65, 0, 0, 0, 0.35)
    aegis.drawText(scoreStr, scoreX, 63, 1.0, 0.95, 0.50)

    local objText = "OBJETIVO: " .. tostring(math.min(hud.coins, hud.requiredCoins)) .. "/" .. tostring(hud.requiredCoins) .. " MOEDAS"
    local objW = 250
    local objX = math.floor((sw - objW) * 0.5)
    aegis.drawRect(objX, 14, objW, 32, 0.08, 0.09, 0.12, 0.78)
    if goalUnlocked() then
        aegis.drawText("PORTA LIBERADA", objX + 46, 23, 0.45, 1.0, 0.68, 1)
    else
        aegis.drawText(objText, objX + 22, 23, 1.0, 0.86, 0.38, 1)
    end
end

local function drawLevelMessage()
    if levelMessageTimer <= 0 or levelMessage == "" then return end
    local sw = aegis.screenWidth()
    local w = math.min(520, sw - 80)
    local x = math.floor((sw - w) * 0.5)
    local y = 86
    local a = math.min(1, levelMessageTimer)
    aegis.drawRect(x, y, w, 34, 0.05, 0.07, 0.10, 0.82 * a)
    aegis.drawRect(x, y, w, 3, 0.40, 0.82, 0.58, 0.95 * a)
    aegis.drawText(levelMessage, x + 18, y + 10, 0.92, 0.96, 0.86, a)
end

local function tutorialConfirmPressed()
    return aegis.padPressed(0, "RB")
        or aegis.padPressed(0, "R1")
        or aegis.padPressed(0, "RightShoulder")
        or aegis.padPressed(0, "A")
        or aegis.padPressed(0, "Start")
        or aegis.keyPressed("Space")
        or aegis.keyPressed("Spacebar")
        or aegis.keyPressed("Enter")
end

local function dismissTutorial()
    tutorial.active = false
    tutorial.alpha = 0
    tutorial.pulse = 0
    GAME.tutorialSeen = true
    jumpHeldPrev = false
    jumpBuffer = 0
end

local function openTutorial()
    tutorial.active = true
    tutorial.alpha = 0
    tutorial.pulse = 0
end

local function updateTutorial(dt)
    if not tutorial.active then return end
    tutorial.alpha = math.min(1, tutorial.alpha + dt * 3.5)
    tutorial.pulse = tutorial.pulse + dt * 4

    if tutorialConfirmPressed() then
        dismissTutorial()
    end
end

local function drawTutorialPopup()
    if not tutorial.active then return end

    local sw, sh = aegis.screenWidth(), aegis.screenHeight()
    local a = tutorial.alpha
    aegis.drawRect(0, 0, sw, sh, 0.02, 0.05, 0.09, 0.72 * a)

    local pw, ph = math.min(520, sw - 80), 360
    local px, py = (sw - pw) * 0.5, (sh - ph) * 0.5

    aegis.drawRect(px - 4, py - 4, pw + 8, ph + 8, 0.35, 0.75, 0.55, 0.35 * a)
    aegis.drawRect(px, py, pw, ph, 0.07, 0.10, 0.14, 0.96 * a)
    aegis.drawRect(px, py, pw, 4, 0.35, 0.75, 0.55, a)

    aegis.drawText("COMO JOGAR", px + 28, py + 22, 0.55, 0.95, 0.78, a)
    aegis.drawText("Analógico esquerdo  —  mover", px + 28, py + 64, 0.88, 0.9, 0.92, a)
    aegis.drawText("A / D no teclado  —  mover", px + 28, py + 88, 0.75, 0.82, 0.88, a)
    aegis.drawText("Botão A  —  pular (pulo no ar 1x)", px + 28, py + 118, 0.88, 0.9, 0.92, a)
    aegis.drawText("colete moedas  ·  evite inimigos vermelhos", px + 28, py + 148, 0.75, 0.82, 0.88, a)
    aegis.drawText("alcance a porta verde para avançar de fase", px + 28, py + 174, 0.75, 0.82, 0.88, a)
    aegis.drawText("ESC  —  voltar ao menu", px + 28, py + 200, 0.6, 0.68, 0.72, a)

    local pulse = 0.55 + 0.45 * math.sin(tutorial.pulse)
    aegis.drawRect(px + 28, py + ph - 58, pw - 56, 40, 0.12, 0.28, 0.22, 0.9 * a)
    aegis.drawText("Pressione  RB / R1  para começar", px + 42, py + ph - 46, 0.4 * pulse + 0.6, 1.0, 0.7, a)
end

local function spawnCoin(x, y)
    local s = aegis.newSprite(ASSETS.coin)
    aegis.setPosition(s, x, y)
    aegis.setZ(s, 20)
    local c = aegis.addCircleCollider(s, 12)
    aegis.setTrigger(c, true)
    aegis.setColliderLayer(c, "PICKUP")
    aegis.setColliderMask(c, "PLAYER")
    aegis.onCollideEnter(c, function(a, b)
        if collectedCoins[s] then return end
        collectedCoins[s] = true
        GAME.score = (GAME.score or 0) + 10
        hud.coins = hud.coins + 1
        syncHud()
        updateGoalState()
        aegis.playSound(ASSETS.sfxCoin)
        aegis.burst(aegis.getX(s)+12, aegis.getY(s)+12, { count=18, speed=90, life=0.45, size=3, r=1, g=0.85, b=0.25 })
        pendingRemoveCoins[#pendingRemoveCoins + 1] = s
    end)
    coins[#coins+1] = s
end

local function spawnCheckpoint(x, y)
    checkpoint = aegis.newRect(26, 52, 0.35, 0.85, 1.0)
    aegis.setPosition(checkpoint, x, y)
    aegis.setZ(checkpoint, 7)
    aegis.setAlpha(checkpoint, 0.62)
    local col = aegis.addCollider(checkpoint, 34, 58, -4, -3)
    aegis.setTrigger(col, true)
    aegis.setColliderLayer(col, "PICKUP")
    aegis.setColliderMask(col, "PLAYER")
    aegis.onCollideEnter(col, function(a, b)
        if checkpointActive then return end
        checkpointActive = true
        checkpointX = x
        checkpointY = y
        aegis.setAlpha(checkpoint, 1.0)
        showLevelMessage("Checkpoint ativado", 1.3)
        aegis.playSound(ASSETS.sfxCoin)
        aegis.burst(x + 13, y + 24, {
            count = 22, speed = 75, life = 0.5, size = 3,
            r = 0.35, g = 0.85, b = 1.0
        })
    end)
end

local function spawnBonusPlatform(x, y, w)
    local p = aegis.newRect(w, 16, 0.58, 0.75, 0.68)
    aegis.setPosition(p, x, y)
    aegis.setZ(p, 3)
    local col = aegis.addCollider(p, w, 16)
    aegis.setColliderLayer(col, "WORLD")
    aegis.setColliderMask(col, "PLAYER|ENEMY")
    return p
end

local function damagePlayer(enemy)
    if hurtCooldown > 0 then return end
    hurtCooldown = 0.9
    GAME.lives = (GAME.lives or 3) - 1
    syncHud()
    aegis.playSoundAt(ASSETS.sfxHurt, aegis.getX(enemy.obj), aegis.getY(enemy.obj), { maxDist = 500 })
    aegis.flashScreen({ r=1, g=0.1, b=0.1 }, 0.12)
    aegis.screenShake(5, 0.18)
    aegis.tween(player, { alpha = 0.35 }, 0.08, "out", function()
        aegis.tween(player, { alpha = 1.0 }, 0.15, "out")
    end)
    if GAME.lives <= 0 then
        aegis.burst(aegis.getX(player)+16, aegis.getY(player)+16, { count=50, speed=160, life=0.75, size=4, r=0.3, g=0.8, b=1.0 })
        aegis.playSound(ASSETS.sfxDeath)
        goToScene("gameover", 0.35)
    end
end

local function spawnEnemy(x, y, patrolLeft, patrolRight)
    local obj = aegis.newSprite(ASSETS.enemy)
    aegis.setPosition(obj, x, y)
    aegis.setZ(obj, 15)
    local enemy = {
        obj        = obj,
        pathTimer  = 0,
        path       = nil,
        pathIndex  = 1,
        -- patrulha: limites horizontais quando não há caminho ao player
        patrolLeft  = patrolLeft  or (x - 80),
        patrolRight = patrolRight or (x + 80),
        patrolDir   = 1,   -- +1 → direita, -1 → esquerda
        patrolSpeed = 55,
    }
    local col = aegis.addCollider(obj, 22, 22, 1, 2)
    aegis.setTrigger(col, true)
    aegis.setColliderLayer(col, "ENEMY")
    aegis.setColliderMask(col, "PLAYER")
    aegis.onCollideEnter(col, function(a, b) damagePlayer(enemy) end)
    enemies[#enemies+1] = enemy
end

function aegis_init()
    aegis.validateAssets()
    aegis.log("[scene] level init: " .. tostring(levelIndex()))
    aegis.clearAll()
    local li = levelIndex()
    local mi = mapIndex()
    -- load tilemap early so we can derive correct world size from the map
    map = aegis.loadTilemap("tilemaps/level" .. mi .. ".json")
    aegis.setZ(map, 0)
    local before = 80 * 30
    local generated = aegis.buildTilemapColliders(map, { solidGids = {1,2,3}, merge = true, layer = "WORLD" })
    aegis.log("level " .. li .. ": tile colliders merge " .. tostring(before) .. " -> " .. tostring(generated))
    nav = aegis.navFromTilemap(map, { solidGids = {1,2,3} })

    -- derive world size from the tilemap (fallback to screen size)
    local mapW = (map.PixelWidth and map.PixelWidth) or aegis.screenWidth()
    local mapH = (map.PixelHeight and map.PixelHeight) or aegis.screenHeight()
    local worldW = math.max(1600, mapW)
    local worldH = math.max(900, mapH)

    -- Camera limits: if the world is smaller than the viewport, center the camera limits
    local sw, sh = aegis.screenWidth(), aegis.screenHeight()
    local camLeft, camTop
    if worldW <= sw then
        camLeft = -((sw - worldW) * 0.5)
    else
        camLeft = 0
    end
    if worldH <= sh then
        camTop = -((sh - worldH) * 0.5)
    else
        camTop = -math.max(0, worldH - 680)
    end
    local camRight = camLeft + worldW
    local camBottom = camTop + worldH
    levelComplete = false
    sceneChanging = false
    enemies, coins = {}, {}
    collectedCoins = {}
    pendingRemoveCoins = {}
    checkpointActive = false
    checkpointX, checkpointY = 48, 380
    levelMessage = ""
    levelMessageTimer = 0
    exitWasUnlocked = false
    hurtCooldown = 0
    coyote = 0
    jumpBuffer = 0
    jumpHeldPrev = false
    airJumpUsed = false
    fallCooldown = 0
    jumpCooldown = 0
    jumpBoostFrames = 0

    aegis.setGroupVolume("sfx", 0.8)
    aegis.setGroupVolume("music", 0.25)
    if not aegis.musicPlaying() then aegis.playMusic(ASSETS.music, true) end

    local sky = aegis.newRect(worldW, worldH, 0.32, 0.39, 0.52)
    aegis.setPosition(sky, 0, camTop)
    aegis.setZ(sky, -50)

    local farMist = aegis.newRect(worldW, 170, 0.38, 0.45, 0.58)
    aegis.setPosition(farMist, 0, 270)
    aegis.setZ(farMist, -49)

    local soilTop = aegis.newRect(worldW, 24, 0.60, 0.64, 0.62)
    aegis.setPosition(soilTop, 0, 486)
    aegis.setZ(soilTop, -48)

    local soilMid = aegis.newRect(worldW, 190, 0.25, 0.30, 0.36)
    aegis.setPosition(soilMid, 0, 510)
    aegis.setZ(soilMid, -49)

    local soilDeep = aegis.newRect(worldW, math.max(220, worldH - 700), 0.18, 0.22, 0.29)
    aegis.setPosition(soilDeep, 0, 700)
    aegis.setZ(soilDeep, -50)

    local lowerShade = aegis.newRect(worldW, math.max(120, worldH - 780), 0.12, 0.15, 0.22)
    aegis.setPosition(lowerShade, 0, 780)
    aegis.setZ(lowerShade, -49)



    player = aegis.newSprite(ASSETS.playerSheet)
    atlas = aegis.loadAtlas(ASSETS.playerAtlas)
    aegis.setAtlasFrame(player, atlas, "idle_00")
    anim = aegis.newAtlasAnimator(player, atlas)
    aegis.addAtlasClip(anim, "idle", { "idle_00" }, 1)
    aegis.addAtlasClip(anim, "run", { "run_00", "run_01", "run_02", "run_03", "run_04", "run_05" }, 8)
    aegis.play(anim, "idle")
    aegis.setPosition(player, 48, 380)
    aegis.setZ(player, 10)
    syncPlayerFacing()
    playerCol = aegis.addCollider(player, 20, 28, 6, 4)
    aegis.setColliderLayer(playerCol, "PLAYER")
    aegis.setColliderMask(playerCol, "WORLD|ENEMY|PICKUP")
    rb = aegis.addRigidbody(player)
    -- setGravity usa multiplicador (1.0 = gravidade padrão do mundo).
    aegis.setGravity(rb, 1.0)
    aegis.setGroundFriction(rb, 0.82)

    aegis.setCameraTarget(player, 7.5)
    aegis.setCameraDeadzone(140, 90)
    aegis.setCameraLookahead(80, 4.0)
    aegis.setCameraLimits(camLeft, camTop, camRight, camBottom)

    buildHud()

    if levelIndex() == 1 and not GAME.tutorialSeen then
        openTutorial()
    end

    spawnCheckpoint(640, 380)

    if li % 2 == 0 then
        spawnBonusPlatform(250, 300, 112)
    end
    if li >= 4 then
        spawnBonusPlatform(690, 250, 126)
    end
    if li >= 6 then
        spawnBonusPlatform(980, 230, 118)
    end

    local coinY = { 335, 175, 225, 285, 290, 405 }
    for i=1,hud.coinMax do
        spawnCoin(120 + i*125, coinY[((i+li-1)%#coinY)+1])
    end

    -- A campanha longa reutiliza os mapas base com perigo crescente por fase.
    spawnEnemy(520 + li*80, 408,  380, 700)
    if li >= 2 then spawnEnemy(880, 280,  760, 1000) end
    if li >= 3 then spawnEnemy(1100, 408, 950, 1190) end
    if li >= 4 then spawnEnemy(320, 335, 180, 450) end
    if li >= 5 then spawnEnemy(1040, 175, 900, 1180) end
    if li >= 7 then spawnEnemy(700, 225, 560, 860) end

    goal = aegis.newRect(34, 72, 0.55, 1.0, 0.65)
    aegis.setPosition(goal, 1210, 380)
    aegis.setZ(goal, 8)
    goalLock = aegis.newRect(44, 82, 1.0, 0.35, 0.25)
    aegis.setPosition(goalLock, 1205, 375)
    aegis.setZ(goalLock, 9)
    aegis.setAlpha(goalLock, 0.72)
    updateGoalState()
    local gcol = aegis.addCollider(goal, 34, 72)
    aegis.setTrigger(gcol, true)
    aegis.setColliderLayer(gcol, "PICKUP")
    aegis.setColliderMask(gcol, "PLAYER")
    aegis.onCollideEnter(gcol, function(a,b)
        if levelComplete then return end
        if not goalUnlocked() then
            local missing = hud.requiredCoins - hud.coins
            showLevelMessage("Colete mais " .. tostring(missing) .. " moeda(s) para abrir a porta", 1.6)
            aegis.playSound(ASSETS.sfxHurt)
            return
        end
        levelComplete = true
        aegis.playSound(ASSETS.sfxLand)
        GAME.level = li + 1
        if GAME.level > GAME.maxLevel then
            GAME.won = true
            aegis.log("[scene] level -> transitionTo(gameover) [won]")
            goToScene("gameover", 0.45)
        else
            aegis.log("[scene] level -> transitionTo(level" .. GAME.level .. ")")
            goToScene("level" .. GAME.level, 0.45)
        end
    end)
end

local function updatePlayer(dt)
    local inputX = 0
    local axis = aegis.padAxis(0, "LeftX")
    if math.abs(axis) > 0.18 then inputX = axis end
    if aegis.keyDown("Left") or aegis.keyDown("A") then inputX = -1 end
    if aegis.keyDown("Right") or aegis.keyDown("D") then inputX = 1 end

    local grounded = aegis.isGrounded(rb)
    if grounded then
        coyote = 0.12
        airJumpUsed = false
    else
        coyote = math.max(0, coyote - dt)
    end

    local targetVx = inputX * MOVE_SPEED
    local currentVx = aegis.getVelocityX(rb)
    local accel = grounded and GROUND_ACCEL or AIR_ACCEL
    local decel = grounded and GROUND_DECEL or AIR_DECEL
    local rate = math.abs(inputX) > 0.05 and accel or decel
    local vx = approach(currentVx, targetVx, rate * dt)
    aegis.setVelocityX(rb, vx)

    if inputX < -0.05 then playerFacing = -1 elseif inputX > 0.05 then playerFacing = 1 end
    syncPlayerFacing()

    local visualGrounded = grounded or coyote > 0.04
    if not visualGrounded then
        if anim then aegis.play(anim, math.abs(vx) > 5 and "run" or "idle") end
    elseif math.abs(vx) > 5 then
        if anim then aegis.play(anim, "run") end
    else
        if anim then aegis.play(anim, "idle") end
    end

    local spaceDown = aegis.keyDown("Space")
    local spacebarDown = aegis.keyDown("Spacebar")
    local mouseHeld = (aegis.mouseLeftDown and aegis.mouseLeftDown()) or false
    local mousePressed = (aegis.mouseLeftJust and aegis.mouseLeftJust()) or false
    local jumpHeld =
        spaceDown or spacebarDown
        or aegis.keyDown("Up") or aegis.keyDown("W") or aegis.keyDown("Z") or aegis.keyDown("Enter")
        or aegis.keyDown("LeftShift") or aegis.keyDown("RightShift")
        or mouseHeld
        or aegis.padDown(0, "A")
    local jumpPressed = (jumpHeld and not jumpHeldPrev)
        or aegis.keyPressed("Space") or aegis.keyPressed("Spacebar")
        or aegis.keyPressed("Up") or aegis.keyPressed("W") or aegis.keyPressed("Z") or aegis.keyPressed("Enter")
        or aegis.keyPressed("LeftShift") or aegis.keyPressed("RightShift")
        or mousePressed
        or aegis.padPressed(0, "A")
    jumpHeldPrev = jumpHeld
    if jumpPressed then jumpBuffer = 0.18 else jumpBuffer = math.max(0, jumpBuffer - dt) end
    -- Fallback robusto: se estiver segurando jump, mantém um buffer curto.
    if jumpHeld then jumpBuffer = math.max(jumpBuffer, 0.08) end

    local vy = aegis.getVelocityY(rb)
    if grounded then
        aegis.setGravity(rb, 1.0)
    elseif vy > 25 then
        aegis.setGravity(rb, 1.38)
    elseif vy < -25 and not jumpHeld then
        aegis.setGravity(rb, 1.22)
    else
        aegis.setGravity(rb, 0.92)
    end

    jumpCooldown = math.max(0, jumpCooldown - dt)
    local canJump = coyote > 0 or (not airJumpUsed)
    if jumpBuffer > 0 and canJump and jumpCooldown <= 0 then
        local currVx = aegis.getVelocityX(rb)
        -- Nudge para sair do contato com o chão antes do impulso vertical.
        aegis.setPosition(player, aegis.getX(player), aegis.getY(player) - 1)
        aegis.setVelocity(rb, currVx, -335)
        jumpBoostFrames = 1
        if coyote <= 0 then
            airJumpUsed = true
        end
        coyote = 0
        jumpBuffer = 0
        jumpCooldown = 0.12
        aegis.playSound(ASSETS.sfxJump)
        aegis.burst(aegis.getX(player)+16, aegis.getY(player)+28, { count=14, speed=80, life=0.35, size=3, r=0.7, g=0.9, b=1.0 })
    end

    if jumpBoostFrames > 0 then
        jumpBoostFrames = jumpBoostFrames - 1
        local currVx = aegis.getVelocityX(rb)
        aegis.setVelocity(rb, currVx, -335)
    end

    if aegis.getY(player) > 900 and fallCooldown <= 0 then
        fallCooldown = 0.8
        GAME.lives = GAME.lives - 1
        syncHud()
        if GAME.lives <= 0 then
            aegis.log("[scene] level -> transitionTo(gameover) [fell]")
            goToScene("gameover", 0.35)
        else
            respawnPlayer()
            aegis.flashScreen({ r=1, g=0.2, b=0.2 }, 0.08)
        end
    end

end

local function updateEnemies(dt)
    for _,e in ipairs(enemies) do
        e.pathTimer = (e.pathTimer or 0) - dt
        if e.pathTimer <= 0 then
            e.pathTimer = 0.35
            e.path = aegis.navFindPath(nav, aegis.getX(e.obj), aegis.getY(e.obj), aegis.getX(player), aegis.getY(player))
            e.pathIndex = 2
            aegis.playSoundAt(ASSETS.sfxEnemy, aegis.getX(e.obj), aegis.getY(e.obj), { maxDist = 420, volume = 0.18 })
        end

        local moved = false
        if e.path and e.path[e.pathIndex] then
            local p = e.path[e.pathIndex]
            local ex,ey = aegis.getX(e.obj), aegis.getY(e.obj)
            local dx,dy = p.x - ex, p.y - ey
            local dist = math.sqrt(dx*dx + dy*dy)
            if dist < 5 then
                e.pathIndex = e.pathIndex + 1
            else
                aegis.move(e.obj, dx / dist * 65 * dt, dy / dist * 65 * dt)
                moved = true
            end
        end

        -- Patrulha lateral quando não há caminho válido ao player
        if not moved then
            local ex = aegis.getX(e.obj)
            if ex >= e.patrolRight then
                e.patrolDir = -1
            elseif ex <= e.patrolLeft then
                e.patrolDir = 1
            end
            aegis.move(e.obj, e.patrolDir * e.patrolSpeed * dt, 0)
        end
    end
end

function aegis_update(dt)
    if sceneChanging then return end

    if tutorial.active then
        updateTutorial(dt)
        return
    end

    hurtCooldown = math.max(0, hurtCooldown - dt)
    fallCooldown = math.max(0, fallCooldown - dt)
    levelMessageTimer = math.max(0, levelMessageTimer - dt)
    if aegis.keyPressed("Escape") or aegis.keyPressed("P") or aegis.padPressed(0, "Start") then
        aegis.log("[scene] level -> pushScene(pause)")
        aegis.pushScene("pause", { level = levelIndex(), score = GAME.score or 0 })
        return
    end
    if #pendingRemoveCoins > 0 then
        for i = 1, #pendingRemoveCoins do
            aegis.removeObject(pendingRemoveCoins[i])
        end
        pendingRemoveCoins = {}
    end
    updatePlayer(dt)
    updateEnemies(dt)
end

function aegis_draw()
end

function aegis_draw_ui()
    if not tutorial.active then
        drawHudChrome()
        drawLevelMessage()
    end
    drawTutorialPopup()
end
