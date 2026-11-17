using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// 一键生成/刷新全部默认数据资产（幂等：已存在则更新数值，不重复创建）。
/// 菜单：Tools/铁皮黎明/生成默认数据
/// </summary>
public static class DataSeeder
{
    const string Root = "Assets/Data";

    [MenuItem("Tools/铁皮黎明/生成默认数据")]
    public static void SeedAll()
    {
        EnsureFolder("Assets/Data/Ammo");
        EnsureFolder("Assets/Data/Weapons");
        EnsureFolder("Assets/Data/Vehicles");
        EnsureFolder("Assets/Data/Zones");
        EnsureFolder("Assets/Data/Match");
        EnsureFolder("Assets/Data/AI");
        EnsureFolder("Assets/Data/Battlefield");

        // ---------- 弹药 ----------
        // ⚠️ 这里的数值必须和 Assets/Data/Ammo/*.asset 的现值**逐字一致**。
        // 否则谁跑一次「Tools/铁皮黎明/生成默认数据」，改过的穿深/倍率就被覆盖回旧值。
        var ap    = Ammo("ap",      "穿甲弹",    1.2f,  0.4f,  0.1f,  0.9f,  5,  260f, false, 12f);
        var he    = Ammo("he",      "高爆弹",    0.5f,  1.5f,  0.6f,  0.8f,  5,   60f);
        var heat  = Ammo("heat",    "破甲弹",    1.0f,  0.9f,  0.1f,  0.9f,  15, 400f, true);
        var aa    = Ammo("aa",      "防空弹",    0.1f,  0.5f,  1.5f,  0.1f,  10,  30f);
        var torp  = Ammo("torpedo", "鱼雷",      0.1f,  0.2f,  0.0f,  1.0f,  30);
        var bomb  = Ammo("bomb",    "航空炸弹",  0.8f,  1.2f,  0.2f,  1.2f,  40);
        // 航空武器：机炮打不穿装甲，导弹才是反坦克/反舰的主力
        var cannon    = Ammo("cannon",     "机炮炮弹",   0.15f, 0.5f, 1.5f, 0.10f, 3,  40f, false, 3f);
        var missileAt = Ammo("missile_at", "反坦克导弹", 2.60f, 1.2f, 0.0f, 1.60f, 60, 500f, true);
        var missileAa = Ammo("missile_aa", "空空导弹",   0.20f, 0.3f, 2.6f, 0.10f, 50, 40f);
        // 2026-11-12：机枪专用弹。以前机枪直接挂 Ammo_ap（穿深 260），12.7mm 能正面打穿主战坦克。
        var mgAmmo    = Ammo("mg",         "机枪弹",     0.15f, 1.2f, 0.47f, 0.20f, 1, 40f);

        // ---------- 武器 ----------
        // 弹速 = 真实炮口初速。原来写 140~250 m/s（约等于弹弓），2 km 图上要飞 4~5 秒，
        // 目标早开出 50 米 —— 这就是"炮弹打不中"的真正根因。现在按真炮取值：
        //   75mm 780 / 120mm 1600 / 130mm 950 / 20mm 机炮 1050 / 舰炮 800 m/s
        // 射程也同步放大到 2 km 图该有的交战距离（原来 160 m 在 2000m 图上等于刺刀）。
        //
        // ⚠️ 与 Assets/Data/Weapons/*.asset 现值保持逐字一致（2026-11-16 收工值）：
        //   舰炮 420/4.0/1300 破甲弹；机炮 4.5；机枪用 Ammo_mg；空空导弹 1 发。
        var gun75  = Weapon("gun_75",   "75mm 炮",     180f, 3.0f,  700f,  780f, Domain.Land, ap, he);
        var gun120 = Weapon("gun_120",  "120mm 主炮",  300f, 4.5f, 1200f, 1600f, Domain.Land, ap, he, heat);
        var gun130 = Weapon("gun_130",  "130mm 重炮",  420f, 6.0f,  900f,  950f, Domain.Land, ap, heat);
        // 2026-11-16：机枪必须用 mg_aa（deckMounted=true 的独立挂点），并且只挂 Ammo_mg。
        // 旧写法是 mg_12 + Ammo_ap（穿深 260）→ 12.7mm 能正面打穿主战坦克 220mm 首上；
        // 而且 mg_12 的 deckMounted=false，DeckWeapon 会返回 null，机枪直接消失。
        var mgAa   = Deck(Weapon("mg_aa", "12.7mm 高射机枪", 14f, 0.11f, 850f, 900f, Domain.Land, mgAmmo));
        // 2026-11-16：舰炮换破甲弹（穿深 400）才打得穿陆军；原来只有高爆弹（穿深 60）连轻坦都打不穿。
        var naval  = Weapon("naval_gun","舰炮",        420f, 4.0f, 1300f,  800f, Domain.Sea,  heat, ap, he);
        var torpL  = Weapon("torpedo_launcher", "鱼雷管", 5500f, 9f, 600f, 50f, Domain.Sea,  torp);
        // 2026-11-16：机炮 60 → 4.5（对空从 600 DPS 压到 45 DPS，否则 0.5 秒一架飞机）
        var gun20  = Weapon("gun_20",   "20mm 机炮",   4.5f, 0.15f, 800f, 1050f, Domain.Air,  cannon, aa);
        var misAt  = Weapon("missile_at","反坦克导弹", 520f, 7.0f, 1400f, 320f, Domain.Air,  missileAt);
        var misAa  = Weapon("missile_aa","空空导弹",   380f, 5.0f, 1600f, 480f, Domain.Air,  missileAa);
        var bombR  = Weapon("bomb_rack","航空炸弹挂架", 750f, 8.0f, 300f,   0f, Domain.Air,  bomb);

        // 备弹数（0/未设 = 无限）。只有飞机挂架有数。
        Cap(misAa, 1);       // 2026-11-16：空空导弹 无限 → 1 发（机翼按备弹挂，1 发就挂 1 枚）
        Cap(misAt, 8);       // 反坦克导弹 8 发

        // ---------- 载具 ----------
        // 参数顺序：id, 名, 域, 价, 血, 装甲模式, 正/侧/背, 统一, 速度, 转向, 炮塔转速,
        //           功率kW, 质量t, 硬地阻力, 软地阻力, 涉水深度, 通行, 复活基础, 说明, 武器
        Vehicle("land_light", "轻坦", Domain.Land, 160, 800, ArmorMode.Directional,
            0.95f, 1.10f, 1.30f, 0f, 18f, 90f, 60f,
            320f, 15f, 0.8f, 1.6f, 1.0f,
            TerrainType.Land | TerrainType.ShallowWater, 80, "快、皮薄，能爬 35° 陡坡", gun75, mgAa);

        Vehicle("land_main", "主战坦克", Domain.Land, 200, 1200, ArmorMode.Directional,
            0.70f, 0.90f, 1.10f, 0f, 12f, 60f, 45f,
            750f, 45f, 0.9f, 1.8f, 1.2f,
            TerrainType.Land | TerrainType.ShallowWater, 80, "均衡，能爬 29°", gun120, mgAa);

        // 重装坦克的 modelYawOffset 保持 0（2026-11-06 模型拆件后长轴已改成 +Z）。
        // 历史上这里写过 90（10-31 的旧模型长轴沿 +X），11-06 已废弃 —— 再写 90 会让炮塔歪 90°。
        Vehicle("land_heavy", "重装坦克", Domain.Land, 220, 1600, ArmorMode.Directional,
            0.60f, 0.80f, 1.05f, 0f, 8f, 40f, 30f,
            800f, 65f, 1.0f, 2.0f, 1.4f,
            TerrainType.Land | TerrainType.ShallowWater, 90, "正面极硬，只能爬 22°", gun130, mgAa);

        // 2026-11-16 用户定：炮艇血量干脆给到 7000（海上霸主，舰炮 420 伤要打 ~17 发）。
        // 注意这是**设计选择**，不是 7.14 缩放的残留 —— 坦克那 3 台已回到 800/1200/1600。
        // 第三把武器是 12.7mm 独立机枪（和陆战载具同一把 mg_aa），别漏。
        Vehicle("sea_gunboat", "炮艇", Domain.Sea, 180, 7000, ArmorMode.Directional,
            0.85f, 1.00f, 1.15f, 0f, 24f, 70f, 50f,
            1200f, 40f, 1.2f, 1.2f, 0.8f,
            TerrainType.ShallowWater | TerrainType.DeepWater, 80, "海上霸主，吃水 0.8m", naval, torpL, mgAa);

        var fighter = Vehicle("air_fighter", "战斗机", Domain.Air, 160, 300, ArmorMode.Uniform,
            1f, 1f, 1f, 1.00f, 45f, 120f, 0f,
            900f, 8f, 0f, 0f, 0f,
            TerrainType.Air, 80, "制空战斗机：机炮 + 空空导弹", gun20, misAa);
        // Lux3D 的飞机模型机头朝 -Z（其余载具朝 +Z）—— 转 180° 正过来，否则看着像倒着飞
        fighter.modelYawOffset = 180f;
        // 喷气机用涡轮啸叫那条循环音（不是坦克柴油机）
        fighter.engineLoopName = Sfx.EngineLoopJet;
        EditorUtility.SetDirty(fighter);

        // 2026-11-16 22:10：炸弹**不挂**。Weapon_bomb_rack 是 hitscan（projectileSpeed=0），
        // 射程只有 300 m，而攻击机巡航高度 220 m —— 射线长度 = 220/sin(俯角)，
        // 只有俯角 ≥ 47° 才够得着，正常对地俯角 20~40° 全部超出射程，实际打不中。
        // 资产留着（bombR 仍创建），要做真炸弹得先改成抛投下坠弹并提射程。
        var attacker = Vehicle("air_attacker", "攻击机", Domain.Air, 200, 450, ArmorMode.Uniform,
            1f, 1f, 1f, 1.10f, 32f, 80f, 0f,
            1100f, 12f, 0f, 0f, 0f,
            TerrainType.Air, 85, "对地攻击机：机炮 + 反坦克导弹", gun20, misAt);
        // 攻击机模型机头本来就朝 +Z，不需要转（战斗机才需要 180）
        attacker.modelYawOffset = 0f;
        // 攻击机走螺旋桨那条（和战斗机音色拉开，一听就能分清是哪架）
        attacker.engineLoopName = Sfx.EngineLoopProp;
        EditorUtility.SetDirty(attacker);

        // ---------- 对局与经济 ----------
        var match = GetOrCreate<MatchDef>($"{Root}/Match/Match_Default.asset");
        match.teamSize = 5;

        // 3 条命规则：玩家部署 3 次；敌方 3 台载具，打光即胜
        match.playerLives = 3;
        match.enemyLives = 3;

        // 车库可选载具
        var roster = new List<VehicleDef>();
        foreach (var id in new[] { "land_light", "land_main", "land_heavy", "sea_gunboat", "air_fighter", "air_attacker" })
        {
            var d = GetOrCreate<VehicleDef>($"{Root}/Vehicles/Vehicle_{id}.asset");
            if (d != null) roster.Add(d);
        }
        match.roster = roster.ToArray();

        // 与 Match_Default.asset 现值一致（资产是 600，这里原来写 200 —— 跑一次 seeder 就把开局金币砍到 1/3）
        match.startingGold = 600;
        match.respawnBase = 80;
        match.respawnGrowth = 1.22f;
        match.swapGrowth = 1.15f;
        match.swapGrowthCap = 3f;
        match.killRewardRate = 0.40f;
        match.assistRewardRate = 0.20f;
        match.captureIncome = 15;
        match.captureInterval = 10f;
        match.survivalIncome = 5;
        match.survivalInterval = 10f;
        match.coinBoxValue = 50;
        match.coinBoxInterval = 25f;
        match.moneyBagValue = 25;
        match.repairCostPerPercent = 1;
        match.ammoRefillCost = 20;
        match.matchTimeLimitMinutes = 15f;
        EditorUtility.SetDirty(match);

        // ---------- 区域（v2：2000×2000 世界坐标，设计坐标 ×16.667）----------
        // 中央 3 个点 15 金/10 秒；家门口 2 个点 5 金/10 秒（躺着收，但不够赢）
        var zPort = Zone("zone_port", "港口海战", Domain.Sea, TerrainType.DeepWater,
            new Rect(-350f, 217f, 700f, 700f), 15,
            new[] { new Vector2(-667f, 500f), new Vector2(667f, 500f) });          // 设计 (0,34) r21
        // 2026-11-16：城区据点挪回陆地。Rect 的中心是 (x + w/2, y + h/2)，
        // 旧值 Rect(-417,-300,834,834) 的中心是 (0, +117)、北边缘 z=534，而岸线在 z≈292 —— 240 m 伸进海里。
        // 新值 Rect(-300,-330,600,600) 覆盖 z -330~270，全在陆地上。
        var zUrban = Zone("zone_urban", "城区巷战", Domain.Land, TerrainType.Land,
            new Rect(-300f, -330f, 600f, 600f), 15,
            new[] { new Vector2(-333f, 117f), new Vector2(333f, 117f) });          // 设计 (0,7) r25
        var zHill = Zone("zone_hill", "山地伏击", Domain.Land, TerrainType.Land,
            new Rect(-200f, -950f, 400f, 400f), 15,
            new[] { new Vector2(-500f, -750f), new Vector2(500f, -750f) });        // 设计 (0,-45) r12
        var zFlankL = Zone("zone_flank_left", "左翼平原", Domain.Land, TerrainType.Land,
            new Rect(-950f, -333f, 400f, 400f), 5,
            new[] { new Vector2(-233f, -133f) });                                  // 设计 (-45,-8) r12
        var zFlankR = Zone("zone_flank_right", "右翼平原", Domain.Land, TerrainType.Land,
            new Rect(550f, -333f, 400f, 400f), 5,
            new[] { new Vector2(233f, -133f) });                                   // 设计 (45,-8) r12

        // ---------- AI ----------
        var ai = GetOrCreate<AIDef>($"{Root}/AI/AI_Default.asset");
        ai.visionRange = 120f;
        ai.fireRange = 90f;
        ai.thinkInterval = 0.5f;
        ai.retreatHpPercent = 0.30f;
        ai.accuracy = 0.70f;
        ai.aggression = 0.60f;
        ai.crossDomainHunting = false;
        ai.crossDomainRetaliation = true;
        ai.retaliationMemorySeconds = 5f;
        ai.swapGoldThreshold = 1.2f;
        ai.repairHpPercent = 0.40f;
        EditorUtility.SetDirty(ai);

        // ---------- 战场（v2：2000×2000）----------
        var bf = GetOrCreate<BattlefieldDef>($"{Root}/Battlefield/Battlefield_Default.asset");
        bf.arenaSize = 2000f;
        // 2026-11-06 修过：±933 正好落在边界山脉里 → 坦克生在山上。
        // 改成 ±640，并且 MatchManager 的出生点扫描带坡度检查（>12° 跳过）。别改回去。
        bf.allySpawnLineX = -640f;
        bf.enemySpawnLineX = 640f;
        bf.spawnLineZMin = -967f;
        bf.spawnLineZMax = 967f;
        // ⚠️ waterLevel 是**死字段**：运行时没有任何代码读它（全走 BattlefieldTerrain.WaterLevel 常量）。
        // 保留只为资产字段完整；真改海平面请改 BattlefieldTerrain.WaterLevel + 场景 Water_Sea 的 Y。
        bf.waterLevel = 4f;
        // 2026-11-16 用户定：据点保持 3 个（港口/城区/山地）。
        // 左翼/右翼平原（zFlankL/zFlankR）资产留着但不引用 —— 补进来会改占点节奏和收益。
        bf.zones = new[] { zPort, zUrban, zHill };
        bf.coinBoxPoints = new[]
        {
            new Vector2(-667f, 500f), new Vector2(667f, 500f),      // 海域
            new Vector2(-333f, 117f), new Vector2(333f, 117f),      // 城区
            new Vector2(-233f, -133f), new Vector2(233f, -133f),    // 中央平原
            new Vector2(-500f, -750f), new Vector2(500f, -750f),    // 山地
        };
        EditorUtility.SetDirty(bf);

        // 注意：这里刻意不调用 AssetDatabase.Refresh()。
        // Refresh 会触发域重载，而本方法常常是在一次工具调用（MCP）里执行的，
        // 域重载会把那次调用打断，导致调用方超时、连接卡死。
        // CreateAsset / SaveAssets 已经足够让资产落盘。
        AssetDatabase.SaveAssets();

        Debug.Log("[DataSeeder] 默认数据已生成：6 载具 / 8 武器 / 6 弹药 / 5 区域 / 1 对局 / 1 AI / 1 战场（2000×2000）");
    }

    // ---------------- 辅助 ----------------

    static AmmoDef Ammo(string id, string name, float vsArmor, float vsSoft, float vsAir, float vsSea, int price,
        float penetration = 0f, bool isChemical = false, float penFalloff = 0f)
    {
        var a = GetOrCreate<AmmoDef>($"{Root}/Ammo/Ammo_{id}.asset");
        a.id = id; a.displayName = name;
        a.vsArmor = vsArmor; a.vsSoft = vsSoft; a.vsAir = vsAir; a.vsSea = vsSea;
        a.pricePerShot = price; a.isSupport = false;
        a.penetration = penetration; a.isChemical = isChemical;
        a.penFalloffPer100m = penFalloff;
        EditorUtility.SetDirty(a);
        return a;
    }

    static WeaponDef Weapon(string id, string name, float dmg, float reload, float range, float speed,
        Domain dom, params AmmoDef[] ammo)
    {
        var w = GetOrCreate<WeaponDef>($"{Root}/Weapons/Weapon_{id}.asset");
        w.id = id; w.displayName = name;
        w.baseDamage = dmg; w.reloadTime = reload; w.range = range; w.projectileSpeed = speed;
        w.mountedDomain = dom; w.ammoTypes = ammo;
        EditorUtility.SetDirty(w);
        return w;
    }

    /// <summary>单独设备弹数（只有飞机挂架类武器需要：导弹 1 发 / 反坦克 8 发）。</summary>
    static WeaponDef Cap(WeaponDef w, int cap)
    {
        if (w != null) { w.ammoCapacity = cap; EditorUtility.SetDirty(w); }
        return w;
    }

    /// <summary>
    /// 把武器标成"独立挂点机枪"（甲板/车顶）：不吃炮塔俯仰限制、可高仰角打飞机、走瞬时命中。
    /// 载具的 DeckWeapon 只认 deckMounted=true 的那把；漏了这个标记机枪就不会出现。
    /// </summary>
    static WeaponDef Deck(WeaponDef w)
    {
        if (w != null)
        {
            w.deckMounted = true;
            w.deckMaxElevation = 85f;
            w.deckMaxDepression = -12f;
            w.deckPitchSpeed = 150f;
            EditorUtility.SetDirty(w);
        }
        return w;
    }

    static VehicleDef Vehicle(string id, string name, Domain dom, int price, float hp, ArmorMode mode,
        float front, float side, float rear, float uniform, float speed, float turn, float turret,
        float power, float mass, float resHard, float resSoft, float wade,
        TerrainType terrain, int respawnBase, string desc, params WeaponDef[] weapons)
    {
        var v = GetOrCreate<VehicleDef>($"{Root}/Vehicles/Vehicle_{id}.asset");
        v.id = id; v.displayName = name; v.domain = dom;
        v.price = price; v.maxHp = hp; v.armorMode = mode;
        v.armorFront = front; v.armorSide = side; v.armorRear = rear; v.uniformMultiplier = uniform;
        v.speed = speed; v.turnSpeed = turn; v.turretTurnSpeed = turret;
        v.enginePower = power; v.mass = mass;
        v.resistanceHard = resHard; v.resistanceSoft = resSoft; v.wadeDepth = wade;
        v.terrainAllowed = terrain; v.respawnBaseCost = respawnBase;
        v.description = desc; v.weapons = weapons;
        EditorUtility.SetDirty(v);
        return v;
    }

    static ZoneDef Zone(string id, string name, Domain dom, TerrainType terrain, Rect bounds,
        int income, Vector2[] coinPoints)
    {
        var z = GetOrCreate<ZoneDef>($"{Root}/Zones/Zone_{id}.asset");
        z.id = id; z.displayName = name; z.domain = dom; z.terrain = terrain;
        z.bounds = bounds; z.captureable = true; z.incomePerTick = income;
        z.coinBoxPoints = coinPoints;
        EditorUtility.SetDirty(z);
        return z;
    }

    static T GetOrCreate<T>(string path) where T : ScriptableObject
    {
        var existing = AssetDatabase.LoadAssetAtPath<T>(path);
        if (existing != null) return existing;
        var created = ScriptableObject.CreateInstance<T>();
        AssetDatabase.CreateAsset(created, path);
        return created;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = System.IO.Path.GetDirectoryName(path).Replace("\\", "/");
        var leaf = System.IO.Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
