using System.Collections.Generic;
using UnityEngine;

public enum TerrainType { None, Wall, Bush, Water, Bridge, Ex, Heal }

public class TerrainGrid : MonoBehaviour
{
    public GameObject terrainTilePrefab;

    // 「中央運河」：1行目がマップ北端（y=17）、18行目が南端（y=0）
    // # 壁 / B ブッシュ / ~ 水場 / = 橋（ネクサス） / E EXマス / H ヒール
    private static readonly string[] Rows =
    {
"......222222......",
"....##222222......",
"....##222B22..B...",
".........####.....",
".##......##...H...",
".##....BB...###...",
".....#........BB..",
".....###E.#.B.....",
"~~==~~~~==~~~~==~~",
"~~==~~~~==~~~~==~~",
".....B.#.E###.....",
"..BB........#.....",
"...###...BB....##.",
"...H...##......##.",
".....####.........",
"...B..11B111##....",
"......111111##....",
"......111111......",
};

    private readonly Dictionary<Vector2Int, TerrainType> map = new();
    private readonly Dictionary<Vector2Int, GameObject> plates = new();
    private readonly Dictionary<Vector2Int, int> terrainHp = new();

    public TerrainType GetTerrain(Vector2Int cell)
    => map.TryGetValue(cell, out var t) ? t : TerrainType.None;

    public int GetTerrainHp(Vector2Int cell)
    => terrainHp.TryGetValue(cell, out int hp) ? hp : 0;

    public bool IsPassable(Vector2Int cell)
    {
        var t = GetTerrain(cell);
        return t != TerrainType.Wall && t != TerrainType.Water;
    }

    // ヒールポイントを使う（成功したら true）。使ったマスは色あせて残る
    public bool ConsumeHeal(Vector2Int cell)
    {
        if (GetTerrain(cell) != TerrainType.Heal) return false;
        map[cell] = TerrainType.None;
        SetPlateColor(cell, new Color(0.85f, 0.78f, 0.78f));
        return true;
    }

    // 地形にダメージを与える。破壊したら元の地形タイプ、しなければ None を返す
    public TerrainType DamageTerrain(Vector2Int cell, int damage)
    {
        TerrainType t = GetTerrain(cell);
        if (t != TerrainType.Wall && t != TerrainType.Bridge)
            return TerrainType.None;
        if (!terrainHp.TryGetValue(cell, out int hp))
            return TerrainType.None;

        hp -= damage;
        terrainHp[cell] = hp;
        if (hp > 0) return TerrainType.None;

        terrainHp.Remove(cell);
        if (t == TerrainType.Wall)
        {
            map[cell] = TerrainType.None; // 焦土（通行可能）
            SetPlateColor(cell, new Color(0.16f, 0.14f, 0.12f));
        }
        else // Bridge は水場に戻る
        {
            map[cell] = TerrainType.Water;
            SetPlateColor(cell, new Color(0.2f, 0.45f, 0.95f));
        }
        return t;
    }

    void SetPlateColor(Vector2Int cell, Color color)
    {
        if (plates.TryGetValue(cell, out var obj))
            obj.GetComponent<SpriteRenderer>().color = color;
    }

    void Awake()
    {
        for (int r = 0; r < 18; r++)
        {
            int y = 17 - r;
            for (int x = 0; x < 18; x++)
            {
                TerrainType t = CharToTerrain(Rows[r][x]);
                if (t == TerrainType.None) continue;
                var cell = new Vector2Int(x, y);
                map[cell] = t;
                if (t == TerrainType.Wall) terrainHp[cell] = 30;
                if (t == TerrainType.Bridge) terrainHp[cell] = 40;
                SpawnTile(cell, t);
            }
        }
    }

    TerrainType CharToTerrain(char c) => c switch
    {
        '#' => TerrainType.Wall,
        'B' => TerrainType.Bush,
        '~' => TerrainType.Water,
        '=' => TerrainType.Bridge,
        'E' => TerrainType.Ex,
        'H' => TerrainType.Heal,
        _ => TerrainType.None,
    };

    void SpawnTile(Vector2Int cell, TerrainType t)
    {
        GameObject obj = Instantiate(terrainTilePrefab);
        obj.transform.position = new Vector3(cell.x + 0.5f, cell.y + 0.5f, 0);
        obj.GetComponent<SpriteRenderer>().color = t switch
        {
            TerrainType.Wall => new Color(0.35f, 0.35f, 0.4f),
            TerrainType.Water => new Color(0.2f, 0.45f, 0.95f),
            TerrainType.Bush => new Color(0.3f, 0.6f, 0.3f),
            TerrainType.Bridge => new Color(0.65f, 0.5f, 0.35f),
            TerrainType.Ex => new Color(0.95f, 0.8f, 0.2f),
            TerrainType.Heal => new Color(0.95f, 0.55f, 0.65f),
            _ => Color.white,
        };
        plates[cell] = obj;
    }
}
