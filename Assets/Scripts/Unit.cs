using UnityEngine;

public class Unit : MonoBehaviour
{
    public string displayName = "ユニット";
    public int team = 1;
    public Vector2Int cell;
    public int mobility = 25;

    [Header("ステータス")]
    public int maxHp = 50;
    public int hp = 50;
    public int attack = 50;    // 攻撃力
    public int defense = 50;   // 防御力
    public int precision = 50; // 精密
    public int agility = 50;   // 敏捷性

    [Header("武器データ")]
    public WeaponData weapon;

    [Header("武器（データから自動入力）")]
    public int weaponRange = 1;
    public int weaponPower = 10;
    public int weaponAccuracy = 80;

    [HideInInspector]
    public bool movedThisTurn = false;
    [HideInInspector]
    public bool hasAttacked = false;

    public int MoveTiles => Mathf.Max(1, mobility / 5);

    void Start()
    {
        if (weapon != null)
        {
            weaponPower = weapon.power;
            weaponAccuracy = weapon.accuracy;
            weaponRange = weapon.range;
        }
        hp = maxHp;
        SnapToCell();
    }

    public void SnapToCell()
    {
        transform.position = new Vector3(cell.x + 0.5f, cell.y + 0.5f, 0);
    }
}
