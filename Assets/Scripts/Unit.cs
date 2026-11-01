using UnityEngine;

public class Unit : MonoBehaviour
{
    public string displayName = "ユニット";
    public int team = 1;
    public Vector2Int cell;   // 今いるマス座標
    public int mobility = 25; // 機動力

    // 移動できるマス数（機動力 ÷ 5、最低1）
    public int MoveTiles => Mathf.Max(1, mobility / 5);

    void Start()
    {
        SnapToCell();
    }

    public void SnapToCell()
    {
        transform.position = new Vector3(cell.x + 0.5f, cell.y + 0.5f, 0);
    }
}
