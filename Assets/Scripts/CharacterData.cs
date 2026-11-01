using UnityEngine;

[CreateAssetMenu(fileName = "Character", menuName = "SquadTactics/Character Data")]
public class CharacterData : ScriptableObject
{
    public string characterName = "キャラ";
    public int maxHp = 50;
    public int attack = 50;    // 攻撃力
    public int defense = 50;   // 防御力
    public int precision = 50; // 精密
    public int agility = 50;   // 敏捷性
    public int mobility = 25;  // 機動力
    public int search = 50;    // 索敵
    public WeaponData defaultWeapon; // 標準の武器
    public EquipmentData[] equipment; // 標準編制（武器以外の持ち込み）

    public string ultName = "";
    [TextArea] public string ultDescription = "";
    public Color color = Color.white; // キャラの色
}
