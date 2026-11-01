using UnityEngine;

public class Unit : MonoBehaviour
{
    public string displayName = "ユニット";
    public int team = 1;
    public Vector2Int cell;
    public CharacterData data;

    [Header("ステータス（データから自動入力）")]
    public int maxHp = 50;
    public int hp = 50;
    public int attack = 50;
    public int defense = 50;
    public int precision = 50;
    public int agility = 50;
    public int mobility = 25;
    public int search = 50;

    [Header("武器（データから自動入力）")]
    public WeaponData weapon;
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
        ApplyData();
    }

    public void ApplyData()
    {
        if (data != null)
        {
            displayName = data.characterName;
            maxHp = data.maxHp;
            attack = data.attack;
            defense = data.defense;
            precision = data.precision;
            agility = data.agility;
            mobility = data.mobility;
            search = data.search;
            weapon = data.defaultWeapon;
        }
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
