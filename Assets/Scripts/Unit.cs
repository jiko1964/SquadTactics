using UnityEngine;

public class Unit : MonoBehaviour
{
    public string displayName = "ユニット";
    public int team = 1;
    public Vector2Int cell;   // 今いるマス座標

    void Start()
    {
        SnapToCell();
    }

    public void SnapToCell()
    {
        // マス座標 → ワールド座標（マスの中心）へ吸着
        transform.position = new Vector3(cell.x + 0.5f, cell.y + 0.5f, 0);
    }
}
