using UnityEngine;

public class UnitSpawner : MonoBehaviour
{
    public GameObject unitPrefab;
    public CharacterData[] team1Characters;
    public CharacterData[] team2Characters;

    // 初期配置（マップの配置エリア内・180度回転対称）
    private readonly Vector2Int[] team1Cells =
        { new(7, 1), new(8, 1), new(9, 1) };
    private readonly Vector2Int[] team2Cells =
        { new(8, 16), new(9, 16), new(10, 16) };

    void Start()
    {
        SpawnTeam(team1Characters, 1, team1Cells, new Color(0.35f, 0.55f, 1f));
        SpawnTeam(team2Characters, 2, team2Cells, new Color(1f, 0.4f, 0.4f));
    }

    void SpawnTeam(CharacterData[] characters, int team,
                   Vector2Int[] cells, Color teamColor)
    {
        for (int i = 0; i < characters.Length; i++)
        {
            GameObject obj = Instantiate(unitPrefab);
            obj.name = characters[i].characterName;

            Unit unit = obj.GetComponent<Unit>();
            unit.data = characters[i];
            unit.team = team;
            unit.cell = cells[i];
            unit.ApplyData();

            // キャラ色とチーム色を半分ずつ混ぜる
            var sr = obj.GetComponent<SpriteRenderer>();
            sr.color = Color.Lerp(characters[i].color, teamColor, 0.5f);
            sr.sortingOrder = 2;
        }
    }
}
