using UnityEngine;

[CreateAssetMenu(fileName = "Weapon", menuName = "SquadTactics/Weapon Data")]
public class WeaponData : ScriptableObject
{
    public string weaponName = "武器";
    public int power = 10;    // 威力
    public int accuracy = 80; // 精度（％）
    public int range = 1;     // 射程
    public int price = 0;     // 価格
}
