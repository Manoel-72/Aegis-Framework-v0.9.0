-- Cena única para 3 fases. Usa tilemap, colisão automática, player, inimigos, coletáveis, HUD e câmera.
local map, nav, player, rb, playerCol, atlas, anim, hud, scoreLabel, lifeLabel
local enemies, coins, goal, jumpEmitter = {}, {}, nil, nil
local collectedCoins = {}
local pendingRemoveCoins = {}
local levelComplete = false
local hurtCooldown = 0
local coyote = 0
local jumpBuffer = 0
local jumpHeldPrev = false
local airJumpUsed = false
local fallCooldown = 0
local jumpCooldown = 0
local dbgJumpPressed = false
local dbgSpaceDown = false
local dbgSpacebarDown = false
local dbgJumpHeld = false
local jumpBoostFrames = 0

local function levelIndex()
    return math.max(1, math.min(3, GAME.level or 1))
end

local function spawnCoin(x, y)
    local s = aegis.newSprite("sprites/coin.png")
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
        aegis.setText(scoreLabel, "Score " .. GAME.score)
        aegis.playSound("coin.wav")
        aegis.burst(aegis.getX(s)+12, aegis.getY(s)+12, { count=18, speed=90, life=0.45, size=3, r=1, g=0.85, b=0.25 })
        pendingRemoveCoins[#pendingRemoveCoins + 1] = s
    end)
    coins[#coins+1] = s
end

local function damagePlayer(enemy)
    if hurtCooldown > 0 then return end
    hurtCooldown = 0.9
    GAME.lives = (GAME.lives or 3) - 1
    aegis.setText(lifeLabel, "Vida " .. GAME.lives)
    aegis.playSoundAt("hurt.wav", aegis.getX(enemy.obj), aegis.getY(enemy.obj), { maxDist = 500 })
    aegis.flashScreen({ r=1, g=0.1, b=0.1 }, 0.12)
    aegis.screenShake(5, 0.18)
    aegis.tween(player, { alpha = 0.35 }, 0.08, "out", function()
        aegis.tween(player, { alpha = 1.0 }, 0.15, "out")
    end)
    if GAME.lives <= 0 then
        aegis.burst(aegis.getX(player)+16, aegis.getY(player)+16, { count=50, speed=160, life=0.75, size=4, r=0.3, g=0.8, b=1.0 })
        aegis.playSound("death.wav")
        aegis.transitionTo("gameover", "fade", 0.35)
    end
end

local function spawnEnemy(x, y, patrolLeft, patrolRight)
    local obj = aegis.newSprite("sprites/enemy.png")
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

local function buildHud()
    local icon = aegis.newSprite("sprites/heart.png")
    lifeLabel = aegis.newLabel("Vida " .. tostring(GAME.lives or 3))
    scoreLabel = aegis.newLabel("Score " .. tostring(GAME.score or 0))
    aegis.setColor(lifeLabel, 1.0, 0.9, 0.92)
    aegis.setColor(scoreLabel, 0.8, 1.0, 0.8)
    hud = aegis.newFlow("horizontal", { gap = 10, padding = 10, align = "center" })
    aegis.flowAdd(hud, icon)
    aegis.flowAdd(hud, lifeLabel)
    aegis.flowAdd(hud, scoreLabel)
    aegis.setPosition(hud, 18, 18)
    aegis.setZ(hud, 500)
    aegis.flowLayout(hud)
end

function aegis_init()
    aegis.log("[scene] level init: " .. tostring(levelIndex()))
    aegis.clearAll()
    local li = levelIndex()
    levelComplete = false
    enemies, coins = {}, {}
    collectedCoins = {}
    pendingRemoveCoins = {}
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
    if not aegis.musicPlaying() then aegis.playMusic("music.wav", true) end

    map = aegis.loadTilemap("tilemaps/level" .. li .. ".json")
    aegis.setZ(map, 0)
    local before = 80 * 30
    local generated = aegis.buildTilemapColliders(map, { solidGids = {1,2,3}, merge = true, layer = "WORLD" })
    aegis.log("level " .. li .. ": tile colliders merge " .. tostring(before) .. " -> " .. tostring(generated))
    nav = aegis.navFromTilemap(map, { solidGids = {1,2,3} })

    player = aegis.newSprite("sprites/player-sheet.png")
    atlas = aegis.loadAtlas("sprites/player-sheet.json")
    aegis.setAtlasFrame(player, atlas, "idle_00")
    anim = aegis.newAtlasAnimator(player, atlas)
    aegis.addAtlasClip(anim, "idle", {"idle_00"}, 1)
    aegis.addAtlasClip(anim, "run", {"run_00", "run_01", "run_02", "run_03", "run_04", "run_05"}, 12)
    aegis.play(anim, "idle")
    aegis.setPosition(player, 48, 320)
    aegis.setZ(player, 10)
    aegis.setShader(player, "outline", { r=0, g=0, b=0, width=2 })
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
    aegis.setCameraLimits(0, 0, 1280, 480)

    buildHud()

    local coinY = { 335, 175, 225, 285, 290 }
    for i=1,5 do spawnCoin(160 + i*150, coinY[((i+li-1)%#coinY)+1]) end

    -- Fase 1: um inimigo patrulhando plataforma central
    spawnEnemy(520 + li*80, 408,  380, 700)
    -- Fases 2 e 3: segundo inimigo numa plataforma elevada
    if li >= 2 then spawnEnemy(880, 280,  760, 1000) end
    -- Fase 3: terceiro inimigo mais agressivo perto da saída
    if li >= 3 then spawnEnemy(1100, 408, 950, 1190) end

    goal = aegis.newRect(34, 72, 0.55, 1.0, 0.65)
    aegis.setPosition(goal, 1210, 380)
    aegis.setZ(goal, 8)
    local gcol = aegis.addCollider(goal, 34, 72)
    aegis.setTrigger(gcol, true)
    aegis.setColliderLayer(gcol, "PICKUP")
    aegis.setColliderMask(gcol, "PLAYER")
    aegis.onCollideEnter(gcol, function(a,b)
        if levelComplete then return end
        levelComplete = true
        aegis.playSound("land.wav")
        GAME.level = li + 1
        if GAME.level > GAME.maxLevel then
            GAME.won = true
            aegis.log("[scene] level -> transitionTo(gameover) [won]")
            aegis.transitionTo("gameover", "fade", 0.45)
        else
            aegis.log("[scene] level -> transitionTo(level" .. GAME.level .. ")")
            aegis.transitionTo("level" .. GAME.level, "fade", 0.45)
        end
    end)
end

local function updatePlayer(dt)
    local vx = 0
    local axis = aegis.padAxis(0, "LeftX")
    if math.abs(axis) > 0.18 then vx = axis * 230 end
    if aegis.keyDown("Left") or aegis.keyDown("A") then vx = -230 end
    if aegis.keyDown("Right") or aegis.keyDown("D") then vx = 230 end
    aegis.setVelocityX(rb, vx)
    if math.abs(vx) > 5 then aegis.play(anim, "run") else aegis.play(anim, "idle") end

    local grounded = aegis.isGrounded(rb)
    if grounded then
        coyote = 0.12
        airJumpUsed = false
    else
        coyote = math.max(0, coyote - dt)
    end

    local spaceDown = aegis.keyDown("Space")
    local spacebarDown = aegis.keyDown("Spacebar")
    dbgSpaceDown = spaceDown
    dbgSpacebarDown = spacebarDown
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
    dbgJumpHeld = jumpHeld
    dbgJumpPressed = jumpPressed
    if jumpPressed then jumpBuffer = 0.18 else jumpBuffer = math.max(0, jumpBuffer - dt) end
    -- Fallback robusto: se estiver segurando jump, mantém um buffer curto.
    if jumpHeld then jumpBuffer = math.max(jumpBuffer, 0.08) end

    jumpCooldown = math.max(0, jumpCooldown - dt)
    local canJump = coyote > 0 or (not airJumpUsed)
    if jumpBuffer > 0 and canJump and jumpCooldown <= 0 then
        local currVx = aegis.getVelocityX(rb)
        -- Nudge para sair do contato com o chão antes do impulso vertical.
        aegis.setPosition(player, aegis.getX(player), aegis.getY(player) - 2)
        aegis.setVelocity(rb, currVx, -430)
        jumpBoostFrames = 2
        if coyote <= 0 then
            airJumpUsed = true
        end
        coyote = 0
        jumpBuffer = 0
        jumpCooldown = 0.12
        aegis.playSound("jump.wav")
        aegis.burst(aegis.getX(player)+16, aegis.getY(player)+28, { count=14, speed=80, life=0.35, size=3, r=0.7, g=0.9, b=1.0 })
    end

    if jumpBoostFrames > 0 then
        jumpBoostFrames = jumpBoostFrames - 1
        local currVx = aegis.getVelocityX(rb)
        aegis.setVelocity(rb, currVx, -430)
    end

    if aegis.getY(player) > 900 and fallCooldown <= 0 then
        fallCooldown = 0.8
        GAME.lives = GAME.lives - 1
        aegis.setText(lifeLabel, "Vida " .. GAME.lives)
        if GAME.lives <= 0 then
            aegis.log("[scene] level -> transitionTo(gameover) [fell]")
            aegis.transitionTo("gameover", "fade", 0.35)
        else
            -- Respawn local para evitar loop de transição e manter controle responsivo.
            aegis.setPosition(player, 48, 320)
            aegis.setVelocity(rb, 0, 0)
            coyote = 0.12
            jumpBuffer = 0
            airJumpUsed = false
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
            aegis.playSoundAt("enemy.wav", aegis.getX(e.obj), aegis.getY(e.obj), { maxDist = 420, volume = 0.18 })
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
    hurtCooldown = math.max(0, hurtCooldown - dt)
    fallCooldown = math.max(0, fallCooldown - dt)
    if aegis.keyPressed("Escape") or aegis.padPressed(0, "Back") then
        aegis.stopMusic()
        aegis.log("[scene] level -> transitionTo(menu) [escape]")
        aegis.transitionTo("menu", "fade", 0.35)
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
    aegis.drawText("Fase " .. tostring(levelIndex()) .. "  |  A/D mover, Space pular, controle funciona", 20, 64, 0.75, 0.9, 0.75)
    aegis.drawText("DBG grounded=" .. tostring(aegis.isGrounded(rb)) .. "  coyote=" .. string.format("%.2f", coyote), 20, 88, 0.95, 0.8, 0.35)
    aegis.drawText("DBG jumpPressed=" .. tostring(dbgJumpPressed) .. "  jumpBuf=" .. string.format("%.2f", jumpBuffer) .. "  vy=" .. string.format("%.1f", aegis.getVelocityY(rb)), 20, 110, 0.75, 0.9, 1.0)
    aegis.drawText("DBG keyDown Space=" .. tostring(dbgSpaceDown) .. "  Spacebar=" .. tostring(dbgSpacebarDown) .. "  jumpHeld=" .. tostring(dbgJumpHeld), 20, 132, 0.9, 0.8, 0.5)
end
