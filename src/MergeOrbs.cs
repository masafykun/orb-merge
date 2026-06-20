using UnityEngine;
using System.Collections.Generic;

// Orb Merge — Suika(スイカゲーム)系の物理マージゲーム。
// コアループ: 上から同サイズのネオンオーブを落とし、同 tier が触れると1つ上の tier に合体して加点。
// 連続合体でコンボ倍率、オーブが DANGER ラインを越えて積み続けると GAME OVER。
// シーンは .unity を手編集せず RuntimeInitializeOnLoadMethod で丸ごとコード生成（AutoShot/Juice と共存）。
// 物理は 3D Sphere に Rigidbody を付け、z=0 平面に拘束（FreezePositionZ＋回転X/Y固定）した擬似2D。
public class MergeOrbs : MonoBehaviour
{
    // --- プレイフィールド寸法（正射影 OrthoSize=6 基準で 16:9 に収める） ---
    public const float OrthoSize = 6f;
    public const float InnerHalfW = 3.5f;     // ジャー内壁の半幅（内寸7）
    public const float FloorY = -5.0f;        // 床の上面
    public const float TopWallY = 4.2f;       // 壁の上端
    public const float SpawnerY = 5.1f;       // 投下スポナー＆プレビューの高さ
    public const float DangerY = 3.4f;        // この上に静止オーブが居続けるとゲームオーバー
    public const float OverflowLimit = 1.6f;  // DANGER 越え許容秒数
    public const float DropCooldown = 0.32f;  // 連続投下の最短間隔
    public const float MoveSpeed = 11f;       // キーボード移動速度
    public const float ComboWindow = 0.7f;    // この秒数内の連続合体でコンボ継続

    public const int MaxTier = 10;            // 0..10 の11段階

    public static MergeOrbs I;

    Camera cam;
    Transform preview;        // 次に落ちるオーブのプレビュー（非物理）
    Transform guide;          // 投下位置の縦ガイドライン
    Renderer dangerRend;      // DANGER ラインの見た目（警告で赤く）
    float spawnerX;
    int nextTier;
    float dropTimer;

    readonly List<Orb> orbs = new List<Orb>();

    int score, best, combo, topTier;
    float lastMergeTime = -10f;
    float overflowTimer;
    bool gameOver;

    // アトラクト（自動投下）: 初期はデモとしてオーブが落ち、スクショ＆動きを見せる。
    // プレイヤーが最初の操作（投下/キー）をした時点で停止する。
    bool attract = true;
    int attractCount;
    float attractTimer = 0.4f;
    Vector3 lastMouse;

    TextMesh hud;
    PhysicsMaterial orbPhys;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("__MergeOrbs");
        go.AddComponent<MergeOrbs>();
        DontDestroyOnLoad(go);
    }

    void Awake()
    {
        I = this;
        Physics.gravity = new Vector3(0f, -22f, 0f); // 重め＝落下がキビキビ気持ちいい
        orbPhys = new PhysicsMaterial { bounciness = 0.0f, dynamicFriction = 0.45f, staticFriction = 0.5f,
            bounceCombine = PhysicsMaterialCombine.Minimum, frictionCombine = PhysicsMaterialCombine.Average };
        BuildScene();
        nextTier = RandomDropTier();
        UpdatePreview();
        UpdateHud();
        lastMouse = Input.mousePosition;
    }

    void BuildScene()
    {
        var stray = GameObject.Find("Cube"); if (stray != null) Destroy(stray);

        // --- カメラ ---
        cam = Camera.main;
        if (cam == null)
        {
            var camGo = new GameObject("Main Camera"); camGo.tag = "MainCamera";
            cam = camGo.AddComponent<Camera>();
        }
        cam.orthographic = true;
        cam.orthographicSize = OrthoSize;
        cam.transform.position = new Vector3(0f, -0.2f, -10f);
        cam.transform.rotation = Quaternion.identity;
        cam.backgroundColor = new Color(0.05f, 0.06f, 0.11f);
        cam.clearFlags = CameraClearFlags.SolidColor;

        // --- ライト（Lit シェーダの陰影用。影は切ってにじみ防止） ---
        var lightGo = new GameObject("Sun");
        var lt = lightGo.AddComponent<Light>();
        lt.type = LightType.Directional;
        lt.intensity = 1.05f;
        lt.color = Color.white;
        lt.shadows = LightShadows.None;
        lightGo.transform.rotation = Quaternion.Euler(40f, -25f, 0f);
        RenderSettings.ambientLight = new Color(0.45f, 0.47f, 0.55f);

        // --- ジャー（左右の壁＋床）。ネオン枠 ---
        var frame = new Color(0.30f, 0.85f, 1.0f);
        float wallH = TopWallY - FloorY;
        float wallCY = (TopWallY + FloorY) * 0.5f;
        MakeWall("WallL", new Vector3(-InnerHalfW - 0.15f, wallCY, 0f), new Vector3(0.3f, wallH, 1.2f), frame);
        MakeWall("WallR", new Vector3(InnerHalfW + 0.15f, wallCY, 0f), new Vector3(0.3f, wallH, 1.2f), frame);
        MakeWall("Floor", new Vector3(0f, FloorY - 0.15f, 0f), new Vector3(InnerHalfW * 2f + 0.6f, 0.3f, 1.2f), frame);
        // 奥行きの見えない壁（z方向にこぼれないよう保険。FreezeZ があるが二重に）
        var backC = new GameObject("BackPlane").AddComponent<BoxCollider>();
        backC.transform.position = new Vector3(0f, wallCY, 0.7f);
        backC.size = new Vector3(InnerHalfW * 2f, wallH, 0.2f);
        var frontC = new GameObject("FrontPlane").AddComponent<BoxCollider>();
        frontC.transform.position = new Vector3(0f, wallCY, -0.7f);
        frontC.size = new Vector3(InnerHalfW * 2f, wallH, 0.2f);

        // --- DANGER ライン ---
        var dl = GameObject.CreatePrimitive(PrimitiveType.Cube);
        dl.name = "DangerLine";
        Destroy(dl.GetComponent<Collider>());
        dl.transform.position = new Vector3(0f, DangerY, 0.2f);
        dl.transform.localScale = new Vector3(InnerHalfW * 2f, 0.05f, 0.05f);
        Paint(dl, new Color(1f, 0.85f, 0.3f), 0.6f);
        dangerRend = dl.GetComponent<Renderer>();

        // --- 投下ガイドライン（縦・薄い） ---
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.name = "Guide";
        Destroy(g.GetComponent<Collider>());
        g.transform.localScale = new Vector3(0.05f, TopWallY - FloorY, 0.05f);
        Paint(g, new Color(0.5f, 0.9f, 1f) * 0.5f, 0.4f);
        var gc = g.GetComponent<Renderer>().material.color; gc.a = 0.4f;
        guide = g.transform;

        // --- プレビュー（次オーブ・非物理） ---
        var pv = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        pv.name = "Preview";
        Destroy(pv.GetComponent<Collider>());
        preview = pv.transform;

        // --- HUD（ワールド空間 TextMesh。左上）。位置は実際のカメラ視野から毎フレーム算出（アスペクト非依存） ---
        hud = MakeText("HUD", Vector3.zero, TextAnchor.UpperLeft);
        PositionHud();

        spawnerX = 0f;
    }

    // HUD を実際の表示領域の左上に貼り付ける。16:9 決め打ちをやめ、どんなアスペクト/リサイズでも見切れない。
    void PositionHud()
    {
        if (hud == null || cam == null) return;
        Vector3 tl = cam.ViewportToWorldPoint(new Vector3(0f, 1f, 10f));
        hud.transform.position = new Vector3(tl.x + 0.3f, tl.y - 0.25f, 0f);
    }

    void MakeWall(string name, Vector3 pos, Vector3 scale, Color c)
    {
        var w = GameObject.CreatePrimitive(PrimitiveType.Cube);
        w.name = name;
        w.transform.position = pos;
        w.transform.localScale = scale;
        var col = w.GetComponent<Collider>();
        if (col is BoxCollider bc) bc.material = orbPhys;
        Paint(w, c, 0.35f);
        var r = w.GetComponent<Renderer>(); r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
    }

    TextMesh MakeText(string name, Vector3 pos, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * 0.16f;
        var tm = go.AddComponent<TextMesh>();
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        tm.font = font;
        tm.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        tm.fontSize = 64; tm.characterSize = 1f; tm.anchor = anchor; tm.color = Color.white;
        return tm;
    }

    // tier ごとの半径（小さな火花→巨大な星へ）。内寸7に収まるよう成長を抑える。
    static float TierRadius(int t) => 0.40f * Mathf.Pow(1.149f, t); // t10 ≒ 1.61（直径3.2）
    static Color TierColor(int t)
    {
        float h = (t * 0.62f) % 1f;              // 黄金角的に色相を回して隣接 tier を見分けやすく
        Color.RGBToHSV(Color.HSVToRGB(h, 1f, 1f), out float hh, out _, out _);
        return Color.HSVToRGB(hh, 0.72f, 1f);
    }
    static int ScoreFor(int t) => (t + 1) * (t + 2) / 2; // 合体で出来た tier に応じて加点（上ほど大きい）

    int RandomDropTier()
    {
        // 投下できるのは小さい tier 0..3（小さいほど出やすい）。
        float r = Random.value;
        if (r < 0.42f) return 0;
        if (r < 0.72f) return 1;
        if (r < 0.90f) return 2;
        return 3;
    }

    void Update()
    {
        PositionHud();
        if (gameOver)
        {
            if (Input.GetKeyDown(KeyCode.R)) Restart();
            return;
        }

        HandleInput();
        UpdatePreview();
        UpdateGuide();
        UpdateAttract();
        CheckOverflow();
        if (dropTimer > 0f) dropTimer -= Time.deltaTime;
    }

    void HandleInput()
    {
        // マウスが動いていればマウス追従、そうでなければキーボードで微調整。
        Vector3 m = Input.mousePosition;
        bool mouseMoved = (m - lastMouse).sqrMagnitude > 4f;
        lastMouse = m;

        // 照準（マウス/キー）はアトラクト中でも効くが、アトラクトを止めるのは「実際の投下」だけ。
        // （ブラウザでマウスが動いただけではデモを止めない＝映え＆操作の発見を促す）
        if (mouseMoved)
        {
            var w = cam.ScreenToWorldPoint(new Vector3(m.x, m.y, 10f));
            spawnerX = w.x;
        }
        float dir = 0f;
        if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) dir -= 1f;
        if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) dir += 1f;
        if (dir != 0f) spawnerX += dir * MoveSpeed * Time.deltaTime;

        float lim = InnerHalfW - TierRadius(nextTier);
        spawnerX = Mathf.Clamp(spawnerX, -lim, lim);

        if ((Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space)) && dropTimer <= 0f)
        {
            attract = false;
            DropAt(spawnerX, nextTier);
            dropTimer = DropCooldown;
            nextTier = RandomDropTier();
        }
    }

    void UpdateAttract()
    {
        if (!attract) return;
        attractTimer -= Time.deltaTime;
        if (attractTimer <= 0f)
        {
            attractTimer = 0.55f;
            float lim = InnerHalfW - 0.6f;
            DropAt(Random.Range(-lim, lim), RandomDropTier());
            attractCount++;
            if (attractCount >= 9) attract = false;
        }
    }

    void DropAt(float x, int tier)
    {
        var orb = SpawnOrb(tier, new Vector3(x, SpawnerY, 0f));
        var rb = orb.GetComponent<Rigidbody>();
        rb.linearVelocity = new Vector3(0f, -2f, 0f); // 少し初速を与えてキビキビ
    }

    Orb SpawnOrb(int tier, Vector3 pos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Orb" + tier;
        float d = TierRadius(tier) * 2f;
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * d;
        Paint(go, TierColor(tier), 0.55f);
        var rend = go.GetComponent<Renderer>();
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;

        var sc = go.GetComponent<SphereCollider>();
        sc.material = orbPhys;

        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 0.5f + tier * 0.3f;
        rb.useGravity = true;
        rb.linearDamping = 0.05f;
        rb.angularDamping = 0.25f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.constraints = RigidbodyConstraints.FreezePositionZ | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationY;

        var o = go.AddComponent<Orb>();
        o.tier = tier;
        orbs.Add(o);
        if (tier > topTier) topTier = tier;
        return o;
    }

    // Orb.OnCollisionEnter から呼ばれる。同 tier 接触で 1回だけ合体処理する。
    public void OnOrbCollision(Orb a, Collision c)
    {
        if (gameOver) return;
        var b = c.collider.GetComponent<Orb>();
        if (b == null || a.merged || b.merged) return;
        if (a.tier != b.tier) return;
        if (a.GetInstanceID() <= b.GetInstanceID()) return; // ペアで片方だけが処理（多重合体ガード）
        DoMerge(a, b);
    }

    void DoMerge(Orb a, Orb b)
    {
        a.merged = b.merged = true;
        orbs.Remove(a); orbs.Remove(b);
        Vector3 mid = (a.transform.position + b.transform.position) * 0.5f;
        int t = a.tier;
        Destroy(a.gameObject); Destroy(b.gameObject);

        // コンボ更新（短時間連続でつながる）。
        if (Time.time - lastMergeTime <= ComboWindow) combo++;
        else combo = 1;
        lastMergeTime = Time.time;
        float mult = 1f + (combo - 1) * 0.5f;

        int gain;
        Color fx = TierColor(t);
        if (t + 1 <= MaxTier)
        {
            SpawnOrb(t + 1, mid);                 // 1つ上のオーブを中点に生成
            gain = ScoreFor(t + 1);
            fx = TierColor(t + 1);
        }
        else
        {
            gain = ScoreFor(MaxTier) * 3;          // 最大同士＝特大ボーナス＆消滅
            Juice.Hit();
            Juice.Pop(mid, Color.white, 22);
        }
        gain = Mathf.RoundToInt(gain * mult);
        score += gain;
        if (score > best) best = score;

        Juice.Score(mid);                          // 効果音＋黄色パーティクル
        Juice.Pop(mid, fx, 12);                    // 合体色の飛散
        float pitch = 1f + Mathf.Min(combo, 8) * 0.06f;  // コンボで音程上昇＝気持ちよさ
        Juice.Blip(660f * pitch, 0.06f, 0.3f);
        UpdateHud();
    }

    // DANGER ライン上に「静止した」オーブが居続けたらゲームオーバー。
    void CheckOverflow()
    {
        bool over = false;
        for (int i = orbs.Count - 1; i >= 0; i--)
        {
            var o = orbs[i];
            if (o == null) { orbs.RemoveAt(i); continue; }
            var rb = o.GetComponent<Rigidbody>();
            float topEdge = o.transform.position.y - TierRadius(o.tier); // 中心-半径＝下端… 実際は上に積もり判定なので中心で見る
            if (o.transform.position.y > DangerY && rb != null && rb.linearVelocity.sqrMagnitude < 1.6f)
                over = true;
        }
        if (over) overflowTimer += Time.deltaTime;
        else overflowTimer = Mathf.Max(0f, overflowTimer - Time.deltaTime * 2f);

        // 警告色（黄→赤）でオーバーフローを可視化。
        if (dangerRend != null)
        {
            float r = Mathf.Clamp01(overflowTimer / OverflowLimit);
            var c = Color.Lerp(new Color(1f, 0.85f, 0.3f), new Color(1f, 0.15f, 0.1f), r);
            dangerRend.material.color = c;
            if (dangerRend.material.HasProperty("_BaseColor")) dangerRend.material.SetColor("_BaseColor", c);
            if (dangerRend.material.HasProperty("_EmissionColor")) dangerRend.material.SetColor("_EmissionColor", c * (0.6f + r));
        }

        if (overflowTimer >= OverflowLimit) GameOver();
    }

    void GameOver()
    {
        gameOver = true;
        if (score > best) best = score;
        Juice.Lose();
        // 一番上のオーブで赤い飛散。
        Orb top = null; float ty = -99f;
        foreach (var o in orbs) if (o != null && o.transform.position.y > ty) { ty = o.transform.position.y; top = o; }
        if (top != null) Juice.Pop(top.transform.position, new Color(1f, 0.3f, 0.2f), 20);
        UpdateHud();
    }

    void Restart()
    {
        for (int i = orbs.Count - 1; i >= 0; i--) if (orbs[i] != null) Destroy(orbs[i].gameObject);
        orbs.Clear();
        score = 0; combo = 0; topTier = 0; overflowTimer = 0f; lastMergeTime = -10f;
        gameOver = false; dropTimer = 0f;
        nextTier = RandomDropTier();
        UpdatePreview(); UpdateHud();
    }

    void UpdatePreview()
    {
        if (preview == null) return;
        float d = TierRadius(nextTier) * 2f;
        preview.position = new Vector3(spawnerX, SpawnerY, 0f);
        preview.localScale = Vector3.one * d;
        var r = preview.GetComponent<Renderer>();
        var c = TierColor(nextTier);
        if (r.sharedMaterial == null || !r.sharedMaterial.HasProperty("_BaseColor")) Paint(preview.gameObject, c, 0.55f);
        else { r.material.color = c; if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c); }
    }

    void UpdateGuide()
    {
        if (guide == null) return;
        float cy = (TopWallY + FloorY) * 0.5f;
        guide.position = new Vector3(spawnerX, cy, 0.2f);
    }

    void UpdateHud()
    {
        if (hud == null) return;
        string comboStr = combo >= 2 ? string.Format("\nCOMBO x{0}", combo) : "";
        hud.text = string.Format("SCORE {0}\nBEST {1}\nTOP {2}/{3}{4}", score, best, topTier, MaxTier, comboStr);
    }

    void OnGUI()
    {
        if (!gameOver) return;
        var center = new GUIStyle(GUI.skin.label)
        { alignment = TextAnchor.MiddleCenter, fontSize = 30, fontStyle = FontStyle.Bold };
        center.normal.textColor = Color.white;
        var rect = new Rect(0f, Screen.height * 0.5f - 60f, Screen.width, 130f);
        GUI.Label(rect, string.Format("OVERFLOW!\nScore {0}   Best {1}\nPress R to Restart", score, best), center);
    }

    static void Paint(GameObject go, Color c, float emit)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var m = new Material(shader);
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (emit > 0f)
        {
            m.EnableKeyword("_EMISSION");
            var ec = c * emit;
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", ec);
        }
        r.material = m;
    }
}

// 各オーブに付く。同 tier 接触をマネージャへ通知する小さなコンポーネント。
public class Orb : MonoBehaviour
{
    public int tier;
    public bool merged;
    void OnCollisionEnter(Collision c)
    {
        if (MergeOrbs.I != null) MergeOrbs.I.OnOrbCollision(this, c);
    }
}
