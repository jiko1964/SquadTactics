using UnityEngine;

public class Unit : MonoBehaviour
{
    public string displayName = "ユニット";
    public int team = 1;
    public Vector2Int cell;
    public CharacterData data;

    public const int MaxEx = 2; // EXの保持上限

    [Header("ステータス（データから自動入力）")]
    public int maxHp = 50;
    public int hp = 50;
    public int attack = 50;
    public int defense = 50;
    public int precision = 50;
    public int agility = 50;
    public int mobility = 25;
    public int search = 50;
    public int armorBonus = 0; // 盾・鎧の防御ボーナス合計

    [Header("シールド（データから自動入力）")]
    public int shieldBonus = 0;      // シールド系だけの防御補正（ハンマーはこれを無視）
    public int shieldBlockRate = 0;  // 0ならブロック不可
    public bool hasCounterShield = false;

    [Header("武器（データから自動入力）")]
    public WeaponData weapon;
    public int weaponRange = 1;
    public int weaponPower = 10;
    public int weaponAccuracy = 80;

    [Header("EXポイント（初期0）")]   //
    public int exPoints = 0;          // 

    [Header("状態異常")]
    public bool poisoned = false;
    public bool beaconed = false;
    public int stunTurns = 0; // 0より大きい間は行動不能

    [HideInInspector]
    public int poisonSourceTeam = 0; // 毒を付与したチーム（毒で倒したときの得点用）
    [HideInInspector]
    public bool movedThisTurn = false;
    [HideInInspector]
    public bool hasAttacked = false;
    [HideInInspector]
    public bool defending = false; // 防御準備をしたらtrue
    [HideInInspector]
    public bool ultGuarding = false; // 鉄壁（ゴウのULT）発動中

    public int MoveTiles => Mathf.Max(1, mobility / 7);

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

            // 装備の反映：防御ボーナス合算と機動力ペナルティ、シールド情報
            armorBonus = 0;
            shieldBonus = 0;
            shieldBlockRate = 0;
            hasCounterShield = false;
            if (data.equipment != null)
            {
                foreach (var item in data.equipment)
                {
                    if (item == null) continue;
                    armorBonus += item.defenseBonus;
                    mobility -= item.mobilityPenalty;
                    if (item.kind == ItemKind.Shield)
                    {
                        shieldBonus += item.defenseBonus;
                        if (item.blockRate > shieldBlockRate)
                        {
                            shieldBlockRate = item.blockRate;
                            hasCounterShield = item.itemName == "カウンターシールド";
                        }
                    }
                }
            }
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
