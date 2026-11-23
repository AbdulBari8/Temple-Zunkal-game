// ============================================================================
//  TEMPLE OF ZUN-KAL  -  3D first-person puzzle-adventure
//  Drop this single file into Assets/Scripts/ of ANY Unity project (2022.3+ / Unity 6),
//  open any scene and press Play. The game builds itself at runtime (no setup needed).
//  Works with both the old Input Manager and the new Input System package.
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

// ---------------------------------------------------------------- input shim
public static class Inp
{
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
    static bool K(System.Func<Keyboard, bool> f) { var k = Keyboard.current; return k != null && f(k); }
    public static Vector2 Move()
    {
        var k = Keyboard.current; if (k == null) return Vector2.zero;
        return new Vector2((k.dKey.isPressed ? 1 : 0) - (k.aKey.isPressed ? 1 : 0),
                           (k.wKey.isPressed ? 1 : 0) - (k.sKey.isPressed ? 1 : 0));
    }
    public static Vector2 Look() { var m = Mouse.current; return m == null ? Vector2.zero : m.delta.ReadValue() * 0.05f; }
    public static bool Sprint() => K(k => k.leftShiftKey.isPressed);
    public static bool Jump()   => K(k => k.spaceKey.wasPressedThisFrame);
    public static bool Esc()    => K(k => k.escapeKey.wasPressedThisFrame);
    public static bool Enter()  => K(k => k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame);
    public static bool Map()    => K(k => k.mKey.wasPressedThisFrame);
#else
    public static Vector2 Move() => new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
    public static Vector2 Look() => new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
    public static bool Sprint() => Input.GetKey(KeyCode.LeftShift);
    public static bool Jump()   => Input.GetKeyDown(KeyCode.Space);
    public static bool Esc()    => Input.GetKeyDown(KeyCode.Escape);
    public static bool Enter()  => Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
    public static bool Map()    => Input.GetKeyDown(KeyCode.M);
#endif
}

// ---------------------------------------------------------------- torch flicker
public class Flicker : MonoBehaviour
{
    Light l; float baseI, seed;
    void Start() { l = GetComponent<Light>(); baseI = l.intensity; seed = Random.value * 100f; }
    void Update() { if (l) l.intensity = baseI * (0.8f + 0.4f * Mathf.PerlinNoise(Time.time * 6f, seed)); }
}

// ---------------------------------------------------------------- the game
public class ZunKalGame : MonoBehaviour
{
    // ===================== TWEAK ME (easy to change during evaluation) =====================
    const float CELL = 4f, WALL_H = 5f;
    const float WALK_SPEED = 5f, RUN_SPEED = 8f, JUMP_SPEED = 6f, GRAVITY = -20f, MOUSE_SENS = 2.2f;
    const int   START_LIVES = 3;
    const float TIME_LIMIT = 300f;      // seconds to find the Heart
    const float COLLAPSE_TIME = 90f;    // seconds left to escape once the Heart is taken
    const int   KEY_SCORE = 100, DOOR_SCORE = 150, HEART_SCORE = 500, ESCAPE_SCORE = 1000;
    const int   HIT_PENALTY = 50, TIME_BONUS = 5 /*per sec left*/, LIFE_BONUS = 200;
    const float BOULDER_SPEED = 6f, BOULDER_R = 1.8f, BOULDER_ROW = 6.5f;
    const int   BOULDER_COL_A = 4, BOULDER_COL_B = 8;

    // Legend: # wall | S start | E sealed entrance (exit) | a b c keys (Sun, Moon, Serpent)
    //         A B C locked doors | ^ timed spikes | ~ pit | H Heart of Eternity
    static readonly string[] MAP = {
        "###############",
        "ES..#...#.....#",
        "#.#.#.#.#.###C#",
        "#.#.a.#.#..H#B#",
        "#.#####.#####A#",
        "#^#.^b#...#c#.#",
        "#.#.......#...#",
        "#.........^.~.#",
        "###############" };

    static readonly string[] KEY_NAMES = { "Sun", "Moon", "Serpent" };
    static readonly Color[] KEY_COLS = { Hex("#E8B84A"), Hex("#BFD8FF"), Hex("#5CE08A") };
    static readonly Color DEEP = Hex("#0C1F1C"), GOLD = Hex("#E8B84A"), TORCH = Hex("#FF8C42"),
                          HEART = Hex("#5FF2E0"), DANGER = Hex("#D9534F");

    // ===================== internals =====================
    enum State { Menu, Playing, Paused, Won, Lost }
    class KeyItem { public int id; public Transform t; public float baseY; public bool got; }
    class DoorItem { public int id; public GameObject go; public Vector3 pos; public bool open; }
    class SpikeItem { public Transform rods; public Vector3 pos; public float off, up; public bool active; }

    const string WORLD = "Temple World";
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<ZunKalGame>() != null) return;   // scene was built with the editor tool
        new GameObject("GameManager").AddComponent<ZunKalGame>();
    }
    static void Kill(Object o) { if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }
    static Color Hex(string h) { Color c; ColorUtility.TryParseHtmlString(h, out c); return c; }

    State state = State.Menu;
    int rows, cols;
    GameObject world;
    CharacterController cc; Transform player, camT; Camera cam;
    Material defaultMat, matWall, matFloor, matMetal, matMetalDark, matWood, matFlame, matGround, matLeaf,
             matPit, matBoulder, matExit, matPedestal, matHeart;
    AudioSource audioSrc; AudioClip sPick, sHit, sDoor, sHeart, sWin, sSlam;
    List<KeyItem> keys = new List<KeyItem>(); List<DoorItem> doors = new List<DoorItem>();
    List<SpikeItem> spikes = new List<SpikeItem>();
    Transform boulder, heartObj; Light heartLight, exitLight; Vector3 startPos, checkpoint, exitPos;
    bool[,] seen;
    float yaw, pitch, velY, clock, hitCooldown, flash, toastT, lockMsgT, boulderX, bdir = 1f;
    string toast = "", playerName = "Explorer", loseReason = "", bonusText = "";
    int score, lives; float timeLeft; bool[] hasKey = new bool[3]; bool hasHeart, showMap, newBest;
    GUIStyle st;

    // -------------------------------------------------------------- setup
    void Awake()
    {
        InitCommon();
        var existing = GameObject.Find(WORLD);
        if (existing) Bind(existing);                       // scene was baked in the editor
        else
        {
            foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);
            BuildLevel();
        }
        playerName = PlayerPrefs.GetString("zk_lastname", "Explorer");
        state = State.Menu; UnlockCursor();
    }

    public void BakeEditor() { InitCommon(); BuildLevel(); }   // used by Zun-Kal > Build Project menu

    void InitCommon()
    {
        var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
        defaultMat = p.GetComponent<Renderer>().sharedMaterial; Kill(p);

        var stone = StoneTex(0.45f, true); var floorTex = StoneTex(0.25f, true); var rough = StoneTex(0.5f, false);
        matWall = Mat(new Color(0.85f, 0.8f, 0.72f), stone, 1.5f, 0f, "Wall_Stone");
        matFloor = Mat(new Color(0.7f, 0.66f, 0.6f), floorTex, 1f, 0f, "Floor_Stone");
        matMetal = Mat(new Color(0.62f, 0.64f, 0.68f), null, 1, 0f, "Metal_Spike"); matMetalDark = Mat(new Color(0.15f, 0.15f, 0.17f), null, 1, 0f, "Metal_Dark");
        matWood = Mat(new Color(0.25f, 0.15f, 0.08f), null, 1, 0f, "Wood"); matFlame = Mat(TORCH, null, 1, 3f, "Torch_Fire");
        matGround = Mat(new Color(0.08f, 0.2f, 0.09f), rough, 20f, 0f, "Jungle_Ground"); matLeaf = Mat(new Color(0.06f, 0.28f, 0.1f), null, 1, 0f, "Jungle_Leaf");
        matPit = Mat(new Color(0.02f, 0.02f, 0.02f), null, 1, 0f, "Pit_Dark"); matBoulder = Mat(new Color(0.75f, 0.7f, 0.62f), rough, 2f, 0f, "Boulder_Stone");
        matExit = Mat(new Color(0.9f, 0.75f, 0.45f), stone, 1.5f, 0.15f, "Entrance_Door"); matPedestal = Mat(new Color(0.55f, 0.52f, 0.46f), stone, 1f, 0f, "Pedestal_Stone");
        matHeart = Mat(HEART, null, 1, 2.5f, "Heart_Glow");

        audioSrc = GetComponent<AudioSource>(); if (!audioSrc) audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false;
        sPick = Tone(660, 1000, 0.3f, 0.5f); sHit = Tone(220, 60, 0.4f, 0.8f, true); sDoor = Tone(110, 70, 0.7f, 0.8f, true);
        sHeart = Tone(300, 1300, 1.1f, 0.5f); sWin = Tone(520, 1050, 1.4f, 0.5f); sSlam = Tone(90, 35, 0.9f, 1f, true);

        RenderSettings.fog = true; RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.03f; RenderSettings.fogColor = DEEP;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.12f, 0.17f, 0.15f);
    }

    // re-attach to a level that already exists in the scene (built by the editor tool)
    void Bind(GameObject w)
    {
        world = w; rows = MAP.Length; cols = MAP[0].Length; seen = new bool[cols, rows];
        keys.Clear(); doors.Clear(); spikes.Clear(); heartObj = null;
        foreach (var t in w.GetComponentsInChildren<Transform>(true))
        {
            string n = t.name;
            if (n.StartsWith("Key_")) keys.Add(new KeyItem { id = System.Array.IndexOf(KEY_NAMES, n.Substring(4)), t = t, baseY = t.position.y });
            else if (n.StartsWith("Door_")) doors.Add(new DoorItem { id = System.Array.IndexOf(KEY_NAMES, n.Substring(5)), go = t.gameObject, pos = new Vector3(t.position.x, 0, t.position.z) });
            else if (n == "SpikeRods")
            {
                int c = Mathf.RoundToInt(t.position.x / CELL), r = Mathf.RoundToInt(-t.position.z / CELL);
                spikes.Add(new SpikeItem { rods = t, pos = new Vector3(t.position.x, 0, t.position.z), off = (c + r) * 0.7f });
            }
            else if (n == "Boulder") boulder = t;
            else if (n == "HeartOfEternity") { heartObj = t; heartLight = t.GetComponentInChildren<Light>(); }
            else if (n == "ExitLight") exitLight = t.GetComponent<Light>();
            else if (n == "Player") { player = t; cc = t.GetComponent<CharacterController>(); }
            else if (n == "PlayerCamera") { camT = t; cam = t.GetComponent<Camera>(); }
        }
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
        { if (MAP[r][c] == 'E') exitPos = CellPos(c, r); if (MAP[r][c] == 'S') startPos = CellPos(c, r); }
        boulderX = BOULDER_COL_A * CELL; bdir = 1f; checkpoint = startPos; yaw = -90f; pitch = 0f; velY = 0f; showMap = false;
        ApplyView();
    }

    Material Mat(Color c, Texture2D tex = null, float tile = 1f, float emis = 0f, string name = "Material")
    {
        var m = new Material(defaultMat); m.color = c; m.name = name;
        if (tex) { m.mainTexture = tex; m.mainTextureScale = new Vector2(tile, tile); }
        if (emis > 0f) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * emis); }
        return m;
    }

    Texture2D StoneTex(float moss, bool bricks)
    {
        int S = 128; var t = new Texture2D(S, S); var px = new Color[S * S]; var rnd = new System.Random(3);
        for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
        {
            float n = Mathf.PerlinNoise(x * 0.07f, y * 0.07f) * 0.5f + Mathf.PerlinNoise(x * 0.31f + 20, y * 0.31f) * 0.35f + (float)rnd.NextDouble() * 0.15f;
            float v = 0.35f + 0.5f * n;
            if (bricks) { int off = ((y / 32) % 2) * 32; if (y % 32 < 2 || (x + off) % 64 < 2) v *= 0.55f; }
            var col = new Color(v, v * 0.97f, v * 0.9f);
            float m = Mathf.PerlinNoise(x * 0.05f + 50, y * 0.05f + 50);
            if (m > 1f - moss) col = Color.Lerp(col, new Color(0.15f, 0.35f, 0.12f), Mathf.Clamp01((m - (1f - moss)) * 6f));
            px[y * S + x] = col;
        }
        t.SetPixels(px); t.wrapMode = TextureWrapMode.Repeat; t.Apply(); return t;
    }

    AudioClip Tone(float f0, float f1, float dur, float vol, bool noise = false)
    {
        int n = (int)(44100 * dur); var d = new float[n]; float ph = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)n; ph += 2f * Mathf.PI * Mathf.Lerp(f0, f1, t) / 44100f;
            float s = noise ? Random.value * 2f - 1f : Mathf.Sin(ph);
            d[i] = s * (1f - t) * (1f - t) * vol;
        }
        var c = AudioClip.Create("tone", n, 1, 44100, false); c.SetData(d, 0); return c;
    }
    void Play(AudioClip c) { audioSrc.PlayOneShot(c); }

    GameObject Prim(PrimitiveType t, Vector3 pos, Vector3 sc, Material m, Transform parent, bool col = true, string name = null)
    {
        var g = GameObject.CreatePrimitive(t); g.transform.position = pos; g.transform.SetParent(parent, true);
        g.transform.localScale = sc; g.GetComponent<Renderer>().sharedMaterial = m;
        if (name != null) g.name = name;
        if (!col) Kill(g.GetComponent<Collider>());
        return g;
    }
    Light MakeLight(Transform parent, Vector3 pos, Color c, float range, float intensity, bool flicker)
    {
        var g = new GameObject("Light"); g.transform.position = pos; g.transform.SetParent(parent, true);
        var l = g.AddComponent<Light>(); l.type = LightType.Point; l.color = c; l.range = range; l.intensity = intensity;
        if (flicker) g.AddComponent<Flicker>(); return l;
    }

    char At(int c, int r) { return (c < 0 || r < 0 || r >= rows || c >= cols) ? '#' : MAP[r][c]; }
    Vector3 CellPos(int c, int r) { return new Vector3(c * CELL, 0f, -r * CELL); }

    // -------------------------------------------------------------- level builder
    readonly Dictionary<string, Transform> groups = new Dictionary<string, Transform>();
    Transform Grp(string path)
    {
        Transform parent = world.transform; string key = "";
        foreach (var part in path.Split('/'))
        {
            key += "/" + part; Transform t;
            if (!groups.TryGetValue(key, out t))
            { var g = new GameObject(part); g.transform.SetParent(parent, false); t = g.transform; groups[key] = t; }
            parent = t;
        }
        return parent;
    }

    void BuildLevel()
    {
        if (world) { world.SetActive(false); Kill(world); }
        world = new GameObject(WORLD); groups.Clear(); var wt = world.transform;
        keys.Clear(); doors.Clear(); spikes.Clear(); heartObj = null;
        rows = MAP.Length; cols = MAP[0].Length; seen = new bool[cols, rows];
        Transform gWalls = Grp("Environment/Walls"), gFloors = Grp("Environment/Floors"), gEntr = Grp("Environment/Entrance"),
                  gGround = Grp("Environment/Ground"), gTrees = Grp("Environment/Jungle Trees"), gTorches = Grp("Lighting/Torches"),
                  gKeys = Grp("Collectibles/Keys"), gHeart = Grp("Collectibles/Heart Chamber"), gDoors = Grp("Puzzle/Locked Doors"),
                  gTraps = Grp("Hazards/Traps");
        int[] dc = { 1, -1, 0, 0 }, dr = { 0, 0, 1, -1 };

        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
        {
            char ch = MAP[r][c]; Vector3 p = CellPos(c, r);
            if (ch == '#') { Prim(PrimitiveType.Cube, p + Vector3.up * WALL_H / 2, new Vector3(CELL, WALL_H, CELL), matWall, gWalls, true, "Wall"); continue; }
            if (ch == 'E')
            {
                exitPos = p;
                Prim(PrimitiveType.Cube, p + Vector3.up * WALL_H / 2, new Vector3(CELL, WALL_H, CELL), matExit, gEntr, true, "Entrance Door");
                Prim(PrimitiveType.Sphere, p + new Vector3(CELL / 2f, 2.4f, 0), new Vector3(0.15f, 2.2f, 2.2f), Mat(GOLD, null, 1, 1.5f, "Entrance_SunDisc"), gEntr, false, "Sun Disc");
                exitLight = MakeLight(gEntr, p + new Vector3(CELL / 2f + 1.5f, 2.4f, 0), GOLD, 9f, 1.5f, false); exitLight.gameObject.name = "ExitLight";
                continue;
            }
            if (ch == '~')
            {
                var pit = new GameObject("Pit"); pit.transform.SetParent(gTraps, false);
                Prim(PrimitiveType.Cube, p + Vector3.down * 8.5f, new Vector3(CELL, 1, CELL), matPit, pit.transform, true, "Pit Bottom");
                for (int i = 0; i < 4; i++)
                {
                    Vector3 o = new Vector3(dc[i], 0, dr[i]) * (CELL / 2f);
                    Vector3 sc = dc[i] != 0 ? new Vector3(0.1f, 8f, CELL) : new Vector3(CELL, 8f, 0.1f);
                    Prim(PrimitiveType.Cube, p + o + Vector3.down * 4f, sc, matPit, pit.transform, false, "Pit Wall");
                }
                MakeLight(pit.transform, p + Vector3.down * 3f, DANGER, 9f, 2f, true);
            }
            else Prim(PrimitiveType.Cube, p + Vector3.down * 0.25f, new Vector3(CELL, 0.5f, CELL), matFloor, gFloors, true, "Floor");

            switch (ch)
            {
                case 'S': startPos = p; break;
                case 'a': case 'b': case 'c': MakeKey(ch - 'a', p, gKeys); break;
                case 'A': case 'B': case 'C': MakeDoor(ch - 'A', p, gDoors); break;
                case '^': MakeSpikes(p, (c + r) * 0.7f, gTraps); break;
                case 'H': MakeHeart(p, gHeart); break;
            }
            if ((ch == '.' || ch == 'S') && ((c * 5 + r * 3) % 7 == 0))
                for (int i = 0; i < 4; i++)
                    if (At(c + dc[i], r + dr[i]) == '#') { MakeTorch(p, new Vector3(dc[i], 0, -dr[i]), gTorches); break; }
        }
        MakeTorch(startPos, Vector3.forward, gTorches);

        float x0 = -2f, x1 = (cols - 1) * CELL + 2f, z1 = 2f, z0 = -(rows - 1) * CELL - 2f, zm = (z0 + z1) / 2f;
        Prim(PrimitiveType.Cube, new Vector3((x0 + x1) / 2f, -0.5f, z1 + 100f), new Vector3(500, 1, 200), matGround, gGround, true, "Ground North");
        Prim(PrimitiveType.Cube, new Vector3((x0 + x1) / 2f, -0.5f, z0 - 100f), new Vector3(500, 1, 200), matGround, gGround, true, "Ground South");
        Prim(PrimitiveType.Cube, new Vector3(x0 - 100f, -0.5f, zm), new Vector3(200, 1, z1 - z0), matGround, gGround, true, "Ground West");
        Prim(PrimitiveType.Cube, new Vector3(x1 + 100f, -0.5f, zm), new Vector3(200, 1, z1 - z0), matGround, gGround, true, "Ground East");
        var rnd = new System.Random(7); int made = 0;
        while (made < 70)
        {
            float x = (float)rnd.NextDouble() * 150f - 45f, z = (float)rnd.NextDouble() * 120f - 85f;
            if (x > x0 - 6f && x < x1 + 6f && z < z1 + 6f && z > z0 - 6f) continue;
            float h = 7f + (float)rnd.NextDouble() * 5f;
            var tree = new GameObject("Tree"); tree.transform.SetParent(gTrees, false);
            Prim(PrimitiveType.Cylinder, new Vector3(x, h / 2f, z), new Vector3(0.7f, h / 2f, 0.7f), matWood, tree.transform, false, "Trunk");
            Prim(PrimitiveType.Sphere, new Vector3(x, h, z), Vector3.one * (6f + (float)rnd.NextDouble() * 3f), matLeaf, tree.transform, false, "Leaves");
            made++;
        }

        boulderX = BOULDER_COL_A * CELL; bdir = 1f;
        boulder = Prim(PrimitiveType.Sphere, new Vector3(boulderX, BOULDER_R, -BOULDER_ROW * CELL), Vector3.one * BOULDER_R * 2f, matBoulder, gTraps, false, "Boulder").transform;

        var pg = new GameObject("Player"); pg.transform.position = startPos + Vector3.up * 0.1f; pg.transform.SetParent(wt, true);
        cc = pg.AddComponent<CharacterController>(); cc.height = 1.8f; cc.radius = 0.4f; cc.center = new Vector3(0, 0.9f, 0); cc.stepOffset = 0.3f;
        player = pg.transform;
        var cg = new GameObject("PlayerCamera"); cg.transform.SetParent(player, false); cg.transform.localPosition = new Vector3(0, 1.6f, 0);
        cam = cg.AddComponent<Camera>(); cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = DEEP;
        cam.fieldOfView = 70f; cam.nearClipPlane = 0.1f; cam.farClipPlane = 150f; cg.AddComponent<AudioListener>(); camT = cg.transform;
        var tl = MakeLight(camT, camT.position + camT.forward * 0.3f, new Color(1f, 0.8f, 0.55f), 15f, 2.6f, true);
        tl.gameObject.name = "ExplorerTorchLight";
        yaw = -90f; pitch = 0f; velY = 0f; checkpoint = startPos; showMap = false;
        ApplyView();
    }

    void MakeTorch(Vector3 p, Vector3 dir, Transform parent)
    {
        Vector3 w = p + dir * (CELL / 2f - 0.2f) + Vector3.up * 2.2f;
        var root = new GameObject("Torch"); root.transform.position = w; root.transform.SetParent(parent, true);
        Prim(PrimitiveType.Cylinder, w, new Vector3(0.14f, 0.35f, 0.14f), matWood, root.transform, false, "Holder");
        Prim(PrimitiveType.Sphere, w + Vector3.up * 0.45f, new Vector3(0.4f, 0.5f, 0.4f), matFlame, root.transform, false, "Flame");
        MakeLight(root.transform, w + Vector3.up * 0.5f - dir * 0.7f, TORCH, 11f, 2.4f, true);
    }

    void MakeKey(int id, Vector3 p, Transform wt)
    {
        var root = new GameObject("Key_" + KEY_NAMES[id]); root.transform.SetParent(wt, true);
        Vector3 b = p + Vector3.up * 1.3f; root.transform.position = b;
        var m = Mat(KEY_COLS[id], null, 1, 1.8f, "Key_" + KEY_NAMES[id]);
        Prim(PrimitiveType.Sphere, b + new Vector3(0, 0.35f, 0), Vector3.one * 0.55f, m, root.transform, false);
        Prim(PrimitiveType.Cylinder, b + new Vector3(0, -0.2f, 0), new Vector3(0.12f, 0.45f, 0.12f), m, root.transform, false);
        Prim(PrimitiveType.Cube, b + new Vector3(0.18f, -0.5f, 0), new Vector3(0.3f, 0.1f, 0.1f), m, root.transform, false);
        Prim(PrimitiveType.Cube, b + new Vector3(0.22f, -0.3f, 0), new Vector3(0.3f, 0.1f, 0.1f), m, root.transform, false);
        MakeLight(root.transform, b, KEY_COLS[id], 7f, 2f, false);
        keys.Add(new KeyItem { id = id, t = root.transform, baseY = b.y });
    }

    void MakeDoor(int id, Vector3 p, Transform wt)
    {
        var m = Mat(Color.Lerp(Color.white, KEY_COLS[id], 0.55f), matWall.mainTexture as Texture2D, 1.5f, 0.35f, "Door_" + KEY_NAMES[id]);
        var g = Prim(PrimitiveType.Cube, p + Vector3.up * WALL_H / 2, new Vector3(CELL * 0.98f, WALL_H, CELL * 0.98f), m, wt);
        g.name = "Door_" + KEY_NAMES[id];
        doors.Add(new DoorItem { id = id, go = g, pos = p });
    }

    void MakeSpikes(Vector3 p, float off, Transform parent)
    {
        var root = new GameObject("SpikeTrap"); root.transform.position = p; root.transform.SetParent(parent, true);
        Prim(PrimitiveType.Cube, p + Vector3.up * 0.04f, new Vector3(CELL * 0.85f, 0.08f, CELL * 0.85f), matMetalDark, root.transform, false, "Base Plate");
        var rods = new GameObject("SpikeRods"); rods.transform.position = p; rods.transform.SetParent(root.transform, true);
        for (int i = -1; i <= 1; i++) for (int j = -1; j <= 1; j++)
            Prim(PrimitiveType.Cylinder, p + new Vector3(i, 0.6f, j), new Vector3(0.2f, 0.6f, 0.2f), matMetal, rods.transform, false, "Spike");
        spikes.Add(new SpikeItem { rods = rods.transform, pos = p, off = off });
    }

    void MakeHeart(Vector3 p, Transform wt)
    {
        Prim(PrimitiveType.Cube, p + new Vector3(0, 0.2f, 0), new Vector3(3f, 0.4f, 3f), matPedestal, wt);
        Prim(PrimitiveType.Cube, p + new Vector3(0, 0.6f, 0), new Vector3(2.4f, 0.4f, 2.4f), matPedestal, wt);
        Prim(PrimitiveType.Cube, p + new Vector3(0, 1.0f, 0), new Vector3(1.8f, 0.4f, 1.8f), matPedestal, wt);
        var root = new GameObject("HeartOfEternity"); root.transform.SetParent(wt, true); root.transform.position = p + Vector3.up * 2.4f;
        var g = Prim(PrimitiveType.Cube, root.transform.position, Vector3.one * 0.95f, matHeart, root.transform, false);
        g.transform.rotation = Quaternion.Euler(45, 45, 0);
        heartObj = root.transform; heartLight = MakeLight(root.transform, root.transform.position, HEART, 16f, 3.5f, false);
    }

    // -------------------------------------------------------------- game flow
    void StartGame()
    {
        playerName = playerName.Replace("|", "").Replace(";", "").Trim(); if (playerName.Length == 0) playerName = "Explorer";
        PlayerPrefs.SetString("zk_lastname", playerName);
        score = 0; lives = START_LIVES; timeLeft = TIME_LIMIT; hasHeart = false; collapsing = false;
        hasKey = new bool[3]; bonusText = ""; showMap = false; hitCooldown = 0;
        RenderSettings.ambientLight = new Color(0.12f, 0.17f, 0.15f);
        state = State.Playing; LockCursor(); Play(sSlam);
        Say("The entrance slams shut! Find the Heart of Eternity and escape.", 4f);
    }
    bool collapsing;

    void NewRun() { BuildLevel(); StartGame(); }
    void ToMenu() { BuildLevel(); state = State.Menu; UnlockCursor(); }
    void SetPaused(bool p) { state = p ? State.Paused : State.Playing; if (p) UnlockCursor(); else LockCursor(); }
    void LockCursor() { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
    void UnlockCursor() { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
    void Say(string s, float t = 3f) { toast = s; toastT = t; }
    void ApplyView() { player.rotation = Quaternion.Euler(0, yaw, 0); camT.localRotation = Quaternion.Euler(pitch, 0, 0); }

    void Update()
    {
        float dt = Time.deltaTime; if (state == State.Paused) dt = 0f;
        if (hitCooldown > 0) hitCooldown -= dt;
        if (flash > 0) flash -= dt * 1.5f;
        if (toastT > 0) toastT -= Time.deltaTime;
        Animate(dt);
        switch (state)
        {
            case State.Menu: if (Inp.Enter() && playerName.Trim().Length > 0) StartGame(); break;
            case State.Playing: PlayGame(dt); break;
            case State.Paused: if (Inp.Esc()) SetPaused(false); break;
            case State.Won: case State.Lost: if (Inp.Enter()) NewRun(); break;
        }
    }

    void PlayGame(float dt)
    {
        if (Inp.Esc()) { SetPaused(true); return; }
        if (Inp.Map()) showMap = !showMap;
        MovePlayer(dt);
        timeLeft -= dt;
        if (timeLeft <= 0f) { Lose(hasHeart ? "The temple collapsed on you!" : "The temple sealed itself forever."); return; }
        int pc = Mathf.RoundToInt(player.position.x / CELL), pr = Mathf.RoundToInt(-player.position.z / CELL);
        for (int a = -1; a <= 1; a++) for (int b = -1; b <= 1; b++)
            if (pc + a >= 0 && pc + a < cols && pr + b >= 0 && pr + b < rows) seen[pc + a, pr + b] = true;
        CheckHazards(); if (state != State.Playing) return;
        Interact();
        if (hasHeart) camT.localPosition = new Vector3(0, 1.6f, 0) + Random.insideUnitSphere * 0.025f;
    }

    void MovePlayer(float dt)
    {
        Vector2 look = Inp.Look() * MOUSE_SENS; yaw += look.x; pitch = Mathf.Clamp(pitch - look.y, -80f, 80f); ApplyView();
        Vector2 mv = Inp.Move(); Vector3 dir = player.right * mv.x + player.forward * mv.y; if (dir.sqrMagnitude > 1f) dir.Normalize();
        if (cc.isGrounded) { if (velY < 0) velY = -2f; if (Inp.Jump()) velY = JUMP_SPEED; }
        velY += GRAVITY * dt;
        cc.Move((dir * (Inp.Sprint() ? RUN_SPEED : WALK_SPEED) + Vector3.up * velY) * dt);
    }

    void CheckHazards()
    {
        Vector3 pp = player.position;
        foreach (var s in spikes)
            if (s.active && Mathf.Abs(pp.x - s.pos.x) < 1.7f && Mathf.Abs(pp.z - s.pos.z) < 1.7f && pp.y < 0.8f) { Hurt("impaled by spikes"); return; }
        float dx = pp.x - boulder.position.x, dz = pp.z - boulder.position.z, rr = BOULDER_R + 0.4f;
        if (dx * dx + dz * dz < rr * rr && pp.y < BOULDER_R) { Hurt("crushed by the boulder"); return; }
        if (pp.y < -3f) Hurt("swallowed by a pit");
    }

    void Hurt(string why)
    {
        if (hitCooldown > 0f || state != State.Playing) return;
        hitCooldown = 1.2f; lives--; score = Mathf.Max(0, score - HIT_PENALTY); flash = 1f; Play(sHit);
        if (lives <= 0) { Lose("You were " + why + "."); return; }
        Say("-" + HIT_PENALTY + " points!  Lives left: " + lives, 2.5f);
        cc.enabled = false; player.position = checkpoint; cc.enabled = true; velY = 0f;
    }

    void Interact()
    {
        Vector3 pp = player.position;
        foreach (var k in keys)
            if (!k.got && Vector3.Distance(pp + Vector3.up, k.t.position) < 1.8f)
            {
                k.got = true; hasKey[k.id] = true; k.t.gameObject.SetActive(false); score += KEY_SCORE; checkpoint = pp; Play(sPick);
                Say(KEY_NAMES[k.id] + " Key collected!  +" + KEY_SCORE, 2.5f);
            }
        foreach (var d in doors)
        {
            if (d.open) continue;
            float dd = Vector2.Distance(new Vector2(pp.x, pp.z), new Vector2(d.pos.x, d.pos.z));
            if (dd > CELL / 2f + 1.6f) continue;
            if (hasKey[d.id])
            {
                d.open = true; d.go.GetComponent<Collider>().enabled = false; score += DOOR_SCORE; Play(sDoor);
                Say("The " + KEY_NAMES[d.id] + " door grinds open!  +" + DOOR_SCORE, 2.5f);
            }
            else if (Time.time > lockMsgT) { Say("Locked! This door needs the " + KEY_NAMES[d.id] + " Key.", 2.5f); lockMsgT = Time.time + 3f; }
        }
        if (!hasHeart && heartObj && Vector3.Distance(pp + Vector3.up, heartObj.position) < 2.4f)
        {
            hasHeart = true; collapsing = true; score += HEART_SCORE; checkpoint = pp; heartObj.gameObject.SetActive(false);
            timeLeft = Mathf.Min(timeLeft, COLLAPSE_TIME); Play(sHeart);
            RenderSettings.ambientLight = new Color(0.3f, 0.08f, 0.06f);
            Say("HEART OF ETERNITY!  +" + HEART_SCORE + "\nThe temple is collapsing - RUN to the entrance!", 5f);
        }
        float de = Vector2.Distance(new Vector2(pp.x, pp.z), new Vector2(exitPos.x, exitPos.z));
        if (de < CELL / 2f + 1.6f)
        {
            if (hasHeart) Win();
            else if (Time.time > lockMsgT) { Say("The entrance is sealed by ancient magic.", 2.5f); lockMsgT = Time.time + 3f; }
        }
    }

    void Win()
    {
        int tb = Mathf.CeilToInt(timeLeft) * TIME_BONUS, lb = lives * LIFE_BONUS;
        score += ESCAPE_SCORE + tb + lb;
        bonusText = "Escape +" + ESCAPE_SCORE + "   Time bonus +" + tb + "   Lives bonus +" + lb;
        SaveResult(true); state = State.Won; Play(sWin); UnlockCursor();
    }
    void Lose(string why) { loseReason = why; SaveResult(false); state = State.Lost; UnlockCursor(); }

    // -------------------------------------------------------------- world animation
    void Animate(float dt)
    {
        clock += dt;
        foreach (var s in spikes)
        {
            float t = (clock + s.off) % 3f, target = t < 1.4f ? 0f : (t < 1.8f ? 0.15f : 1f);
            s.up = Mathf.MoveTowards(s.up, target, dt * (target > s.up ? 9f : 3f)); s.active = s.up > 0.7f;
            s.rods.position = new Vector3(s.pos.x, Mathf.Lerp(-1.4f, 0f, s.up), s.pos.z);
        }
        foreach (var k in keys)
            if (!k.got) { k.t.Rotate(0, 120f * dt, 0); k.t.position = new Vector3(k.t.position.x, k.baseY + Mathf.Sin(clock * 2f) * 0.12f, k.t.position.z); }
        if (heartObj && heartObj.gameObject.activeSelf)
        {
            heartObj.Rotate(0, 60f * dt, 0, Space.World);
            heartObj.localScale = Vector3.one * (1f + 0.08f * Mathf.Sin(clock * 3f)); heartLight.intensity = 3.5f + Mathf.Sin(clock * 3f);
        }
        if (exitLight) exitLight.intensity = hasHeart ? 3.5f + 2f * Mathf.Sin(clock * 8f) : 1.5f;
        boulderX += bdir * BOULDER_SPEED * dt;
        float minX = BOULDER_COL_A * CELL, maxX = BOULDER_COL_B * CELL;
        if (boulderX > maxX) { boulderX = maxX; bdir = -1f; } else if (boulderX < minX) { boulderX = minX; bdir = 1f; }
        boulder.position = new Vector3(boulderX, BOULDER_R, -BOULDER_ROW * CELL);
        boulder.Rotate(0, 0, -bdir * BOULDER_SPEED * dt / BOULDER_R * Mathf.Rad2Deg, Space.World);
        foreach (var d in doors)
            if (d.open && d.go.activeSelf)
            {
                d.go.transform.position += Vector3.down * 3f * dt;
                if (d.go.transform.position.y < -WALL_H / 2f - 0.5f) d.go.SetActive(false);
            }
    }

    // -------------------------------------------------------------- profile & leaderboard
    void SaveResult(bool won)
    {
        string n = playerName;
        PlayerPrefs.SetInt("zk_games_" + n, PlayerPrefs.GetInt("zk_games_" + n, 0) + 1);
        if (won) PlayerPrefs.SetInt("zk_wins_" + n, PlayerPrefs.GetInt("zk_wins_" + n, 0) + 1);
        newBest = score > PlayerPrefs.GetInt("zk_best_" + n, 0);
        if (newBest) PlayerPrefs.SetInt("zk_best_" + n, score);
        var board = LoadBoard(); board.Add(new KeyValuePair<string, int>(n, score));
        board.Sort((a, b) => b.Value.CompareTo(a.Value)); if (board.Count > 5) board.RemoveRange(5, board.Count - 5);
        var parts = new List<string>(); foreach (var e in board) parts.Add(e.Key + "|" + e.Value);
        PlayerPrefs.SetString("zk_board", string.Join(";", parts)); PlayerPrefs.Save();
    }
    List<KeyValuePair<string, int>> LoadBoard()
    {
        var list = new List<KeyValuePair<string, int>>(); string raw = PlayerPrefs.GetString("zk_board", "");
        foreach (var part in raw.Split(';'))
        {
            var kv = part.Split('|'); int v;
            if (kv.Length == 2 && int.TryParse(kv[1], out v)) list.Add(new KeyValuePair<string, int>(kv[0], v));
        }
        return list;
    }
    string BoardText()
    {
        var b = LoadBoard(); if (b.Count == 0) return "No scores yet - be the first!";
        string s = ""; for (int i = 0; i < b.Count; i++) s += (i + 1) + ".  " + b[i].Key + "  -  " + b[i].Value + "\n"; return s;
    }

    // -------------------------------------------------------------- GUI
    float U { get { return Screen.height / 720f; } }
    void Box(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }
    void Txt(Rect r, string s, int size, Color c, TextAnchor a = TextAnchor.MiddleCenter, FontStyle fs = FontStyle.Bold)
    {
        if (st == null) { st = new GUIStyle(GUI.skin.label); st.richText = true; st.wordWrap = true; }
        st.fontSize = Mathf.RoundToInt(size * U); st.alignment = a; st.fontStyle = fs; st.normal.textColor = c; GUI.Label(r, s, st);
    }
    bool Btn(Rect r, string s)
    {
        bool hov = r.Contains(Event.current.mousePosition);
        Box(r, hov ? GOLD : new Color(0.45f, 0.33f, 0.1f, 0.95f));
        Txt(r, s, 24, hov ? DEEP : Color.white);
        return GUI.Button(r, GUIContent.none, GUIStyle.none);
    }
    void Shadow(Rect r, string s, int size, Color c)
    { Txt(new Rect(r.x + 2, r.y + 2, r.width, r.height), s, size, Color.black); Txt(r, s, size, c); }

    void OnGUI()
    {
        float W = Screen.width, H = Screen.height, u = U, cx = W / 2f;
        if (state == State.Playing || state == State.Paused) DrawHud(W, H, u);
        if (state == State.Menu) DrawMenu(W, H, u, cx);
        else if (state == State.Paused)
        {
            Box(new Rect(0, 0, W, H), new Color(0, 0, 0, 0.7f));
            Shadow(new Rect(0, 150 * u, W, 80 * u), "PAUSED", 56, GOLD);
            if (Btn(new Rect(cx - 150 * u, 280 * u, 300 * u, 56 * u), "RESUME")) SetPaused(false);
            if (Btn(new Rect(cx - 150 * u, 350 * u, 300 * u, 56 * u), "RESTART")) NewRun();
            if (Btn(new Rect(cx - 150 * u, 420 * u, 300 * u, 56 * u), "MAIN MENU")) ToMenu();
        }
        else if (state == State.Won || state == State.Lost) DrawEnd(W, H, u, cx);
    }

    void DrawMenu(float W, float H, float u, float cx)
    {
        Box(new Rect(0, 0, W, H), new Color(0.02f, 0.06f, 0.05f, 0.82f));
        Shadow(new Rect(0, 24 * u, W, 70 * u), "TEMPLE OF ZUN-KAL", 54, GOLD);
        Txt(new Rect(0, 92 * u, W, 30 * u), "3D PUZZLE-ADVENTURE", 20, TORCH);
        Txt(new Rect(0, 128 * u, W, 24 * u), "EXPLORER NAME", 16, Color.white);
        var tf = new GUIStyle(GUI.skin.textField) { fontSize = Mathf.RoundToInt(24 * u), alignment = TextAnchor.MiddleCenter };
        playerName = GUI.TextField(new Rect(cx - 180 * u, 154 * u, 360 * u, 40 * u), playerName, 14, tf);
        string n = playerName.Replace("|", "").Replace(";", "").Trim();
        Txt(new Rect(0, 198 * u, W, 24 * u), "Games: " + PlayerPrefs.GetInt("zk_games_" + n, 0) + "    Wins: " + PlayerPrefs.GetInt("zk_wins_" + n, 0) +
            "    Best score: " + PlayerPrefs.GetInt("zk_best_" + n, 0), 16, HEART);
        Txt(new Rect(cx - 380 * u, 232 * u, 760 * u, 190 * u),
            "<color=#E8B84A>GOAL</color>  Take the Heart of Eternity, then run back to the entrance before the temple seals forever.\n" +
            "<color=#E8B84A>KEYS</color>  Find the Sun, Moon and Serpent keys to open the three stone doors.\n" +
            "<color=#E8B84A>DANGER</color>  Spikes, pits and the rolling boulder cost a life (3 lives).\n" +
            "<color=#E8B84A>CONTROLS</color>  WASD move  |  Mouse look  |  Shift run  |  Space jump  |  M map  |  Esc pause",
            19, Color.white, TextAnchor.UpperLeft, FontStyle.Normal);
        if (Btn(new Rect(cx - 150 * u, 430 * u, 300 * u, 56 * u), "ENTER THE TEMPLE") && n.Length > 0) StartGame();
        Txt(new Rect(cx - 200 * u, 500 * u, 400 * u, 26 * u), "TOP EXPLORERS", 18, GOLD);
        Txt(new Rect(cx - 200 * u, 526 * u, 400 * u, 170 * u), BoardText(), 18, Color.white, TextAnchor.UpperCenter, FontStyle.Normal);
    }

    void DrawEnd(float W, float H, float u, float cx)
    {
        bool won = state == State.Won;
        Box(new Rect(0, 0, W, H), new Color(won ? 0.02f : 0.18f, won ? 0.1f : 0.02f, won ? 0.08f : 0.02f, 0.88f));
        Shadow(new Rect(0, 60 * u, W, 80 * u), won ? "YOU ESCAPED!" : "GAME OVER", 62, won ? HEART : DANGER);
        Txt(new Rect(0, 150 * u, W, 36 * u), won ? "The Heart of Eternity is yours, " + playerName + "." : loseReason, 24, Color.white);
        Shadow(new Rect(0, 200 * u, W, 60 * u), "SCORE  " + score, 44, GOLD);
        if (won) Txt(new Rect(0, 258 * u, W, 30 * u), bonusText, 18, TORCH);
        if (newBest) Txt(new Rect(0, 290 * u, W, 30 * u), "NEW PERSONAL BEST!", 22, HEART);
        Txt(new Rect(cx - 200 * u, 335 * u, 400 * u, 26 * u), "TOP EXPLORERS", 18, GOLD);
        Txt(new Rect(cx - 200 * u, 360 * u, 400 * u, 130 * u), BoardText(), 18, Color.white, TextAnchor.UpperCenter, FontStyle.Normal);
        if (Btn(new Rect(cx - 320 * u, 530 * u, 200 * u, 56 * u), "PLAY AGAIN")) NewRun();
        if (Btn(new Rect(cx - 100 * u, 530 * u, 200 * u, 56 * u), "MAIN MENU")) ToMenu();
        if (Btn(new Rect(cx + 120 * u, 530 * u, 200 * u, 56 * u), "QUIT")) Application.Quit();
    }

    void DrawHud(float W, float H, float u)
    {
        if (flash > 0f) Box(new Rect(0, 0, W, H), new Color(0.85f, 0.1f, 0.1f, 0.45f * flash));
        if (hasHeart) Box(new Rect(0, 0, W, H), new Color(0.8f, 0.1f, 0.05f, 0.07f + 0.05f * Mathf.Sin(clock * 6f)));
        Box(new Rect(0, 0, W, 50 * u), new Color(0, 0, 0, 0.6f));
        Txt(new Rect(16 * u, 0, 400 * u, 50 * u), playerName + "    <color=#E8B84A>Score " + score + "</color>", 22, Color.white, TextAnchor.MiddleLeft);
        int m = Mathf.Max(0, Mathf.FloorToInt(timeLeft)) / 60, s = Mathf.Max(0, Mathf.FloorToInt(timeLeft)) % 60;
        bool urgent = hasHeart || timeLeft < 30f;
        Txt(new Rect(W / 2 - 100 * u, 0, 200 * u, 50 * u), string.Format("{0:00}:{1:00}", m, s), 32, urgent ? DANGER : Color.white);
        string hearts = ""; for (int i = 0; i < START_LIVES; i++) hearts += i < lives ? "<color=#D9534F>O</color> " : "<color=#444444>O</color> ";
        Txt(new Rect(W - 300 * u, 0, 284 * u, 50 * u), "LIVES  " + hearts, 22, Color.white, TextAnchor.MiddleRight);
        for (int i = 0; i < 4; i++)
        {
            Rect r = new Rect(16 * u + i * 92 * u, 58 * u, 84 * u, 26 * u);
            bool got = i < 3 ? hasKey[i] : hasHeart; Color c = i < 3 ? KEY_COLS[i] : HEART;
            Box(r, got ? c : new Color(0.15f, 0.15f, 0.15f, 0.7f));
            Txt(r, i < 3 ? KEY_NAMES[i].ToUpper() : "HEART", 14, got ? DEEP : new Color(0.6f, 0.6f, 0.6f));
        }
        string obj = hasHeart ? "ESCAPE!  Return to the entrance door!" : "Reach the Heart of Eternity   (keys " + (hasKey[0] ? 1 : 0 + 0) + "/3)";
        if (!hasHeart) { int kc = 0; foreach (var b in hasKey) if (b) kc++; obj = "Reach the Heart of Eternity   (keys " + kc + "/3)"; }
        Txt(new Rect(0, H - 50 * u, W, 40 * u), obj, 22, hasHeart ? DANGER : GOLD);
        Box(new Rect(W / 2 - 2, H / 2 - 2, 4, 4), new Color(1, 1, 1, 0.6f));
        if (toastT > 0f) Shadow(new Rect(W / 2 - 450 * u, 110 * u, 900 * u, 120 * u), toast, 26, Color.white);
        if (showMap) DrawMap(W, H, u);
    }

    void DrawMap(float W, float H, float u)
    {
        float cs = 26 * u, ox = W / 2 - cols * cs / 2, oy = H / 2 - rows * cs / 2;
        Box(new Rect(ox - 12 * u, oy - 12 * u, cols * cs + 24 * u, rows * cs + 24 * u), new Color(0, 0, 0, 0.75f));
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
        {
            if (!seen[c, r]) continue; char ch = MAP[r][c]; Color col;
            if (ch == '#') col = new Color(0.3f, 0.28f, 0.25f);
            else if (ch == 'E') col = GOLD;
            else if (ch == '~') col = Color.black;
            else if (ch == '^') col = DANGER;
            else if (ch == 'H') col = HEART;
            else if (ch >= 'A' && ch <= 'C') col = doors[ch - 'A'].open ? new Color(0.5f, 0.45f, 0.35f) : KEY_COLS[ch - 'A'];
            else if (ch >= 'a' && ch <= 'c') col = keys[ch - 'a'].got ? new Color(0.5f, 0.45f, 0.35f) : KEY_COLS[ch - 'a'];
            else col = new Color(0.55f, 0.5f, 0.4f);
            Box(new Rect(ox + c * cs, oy + r * cs, cs - 2, cs - 2), col);
        }
        float px = ox + player.position.x / CELL * cs, py = oy + (-player.position.z) / CELL * cs;
        Box(new Rect(px - 6 * u, py - 6 * u, 12 * u, 12 * u), Color.white);
    }
}
